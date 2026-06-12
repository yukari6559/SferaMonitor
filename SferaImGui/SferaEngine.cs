using System.Runtime.InteropServices;
using System.Text;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;


public class SferaEngine
{
    const int RING_SECONDS = 4;
    const int SAMPLE_RATE = 96000;
    const int RING_SIZE = RING_SECONDS * SAMPLE_RATE;
    const int BLOCK_SIZE = 2048;
	const int SWAP_PREROLL = BLOCK_SIZE * 4;   // 8192 samples

    private IntPtr _stream;
    private List<float> _wChannel = new();
    private List<float> _xChannel = new();
    private List<float> _yChannel = new();
    private List<float> _zChannel = new();
    private Dictionary<string, (List<float> L, List<float> R)> _hrirCache = new();

    public float Azimuth = 0f;
    public float Elevation = 0f;
    public string HrirDir = "/Users/mths40035/Downloads/D2/D2_HRIR_WAV/96K_24bit";
    private string _currentHrirFile = "";

    private int _playbackSamplePos = 0;
    private bool _running = false;
    private Thread? _fillThread;

	private class AudioBuffer
	{
		public float[] RingL = new float[RING_SIZE];
		public float[] RingR = new float[RING_SIZE];
		public int ReadPos;
		public int WritePos;

		public float[] OverlapL = new float[511];
		public float[] OverlapR = new float[511];

		public int SourcePos;
	}


	private AudioBuffer _bufferA = new();
	private AudioBuffer _bufferB = new();

	private volatile AudioBuffer _activeBuffer = null!;
	private volatile AudioBuffer _standbyBuffer = null!;


    private PortAudioWrapper.Pa_StreamCallbackDelegate? _callbackDelegate;

    public bool IsLoaded => _wChannel.Count > 0;
    public bool IsPlaying => _running;


	private volatile bool _directionChangeRequested = false;
	private volatile bool _standbyReady = false;
	private volatile int _standbyBaseSamplePos = 0;

	private float _requestedAzimuth = 0f;
	private float _requestedElevation = 0f;
	private string _requestedHrirFile = "";

	private AudioBuffer? _fadeFromBuffer;
	private int _fadeSamplesLeft = 0;
	private const int CROSSFADE_SAMPLES = SAMPLE_RATE / 8; // 0.125秒クロスフェード



    int RingBuffered(AudioBuffer buf) => (buf.WritePos - buf.ReadPos + RING_SIZE) % RING_SIZE;

    public void LoadWav(string filePath)
    {
        _wChannel.Clear();
        _xChannel.Clear();
        _yChannel.Clear();
        _zChannel.Clear();

        using var reader = new BinaryReader(File.OpenRead(filePath));
        reader.ReadBytes(4);
        reader.ReadBytes(4);
        reader.ReadBytes(4);

        short channels = 0;
        short bitsPerSample = 0;
        uint dataSize = 0;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            uint chunkSize = reader.ReadUInt32();

            if (chunkId == "fmt ")
            {
                reader.ReadBytes(2);
                channels = reader.ReadInt16();
                reader.ReadInt32(); // sampleRate
                reader.ReadBytes(4);
                reader.ReadBytes(2);
                bitsPerSample = reader.ReadInt16();
                int remaining = (int)chunkSize - 16;
                if (remaining > 0) reader.ReadBytes(remaining);
                if (chunkSize % 2 != 0) reader.ReadByte();
            }
            else if (chunkId == "data")
            {
                dataSize = chunkSize;
                int totalFrames = (int)(dataSize / (channels * (bitsPerSample / 8)));
                for (int i = 0; i < totalFrames; i++)
                {
                    float[] frame = new float[channels];
                    for (int ch = 0; ch < channels; ch++)
                    {
                        byte[] buf = reader.ReadBytes(3);
                        int rawInt = buf[0] | (buf[1] << 8) | (buf[2] << 16);
                        if ((rawInt & 0x800000) != 0)
                            rawInt |= unchecked((int)0xFF000000);
                        frame[ch] = rawInt / 8388607f;
                    }
                    _wChannel.Add(frame[0]);
                    _xChannel.Add(frame[1]);
                    _yChannel.Add(frame[2]);
                    _zChannel.Add(frame[3]);
                }
                break;
            }
            else
            {
                reader.ReadBytes((int)chunkSize);
                if (chunkSize % 2 != 0) reader.ReadByte();
            }
        }
    }

    public void LoadAllHrirs()
    {
        var files = Directory.GetFiles(HrirDir, "*.wav");
        foreach (var file in files)
        {
            var (l, r) = ReadHrirFile(file);
            _hrirCache[file] = (l, r);
        }
        Console.WriteLine($"HRIR {_hrirCache.Count}件読み込み完了");
    }

    
	public void Play()
	{
		if (!IsLoaded) return;
		if (_hrirCache.Count == 0) LoadAllHrirs();

		_currentHrirFile = GetNearestHrirFile(Azimuth, Elevation);
		_playbackSamplePos = 0;

		_fadeFromBuffer = null;
		_fadeSamplesLeft = 0;

		PortAudioWrapper.Pa_Initialize();

		_activeBuffer = _bufferA;
		_standbyBuffer = _bufferB;

		ClearBuffer(_activeBuffer, 0);
		ClearBuffer(_standbyBuffer, 0);

		_callbackDelegate = new PortAudioWrapper.Pa_StreamCallbackDelegate(Callback);
		var callbackPtr = Marshal.GetFunctionPointerForDelegate(_callbackDelegate);
		PortAudioWrapper.Pa_OpenDefaultStream(out _stream, 0, 2, 0x00000001, 96000, 256, callbackPtr, IntPtr.Zero);

		_running = true;
		_fillThread = new Thread(FillRingBuffer) { IsBackground = true };
		_fillThread.Start();

		PortAudioWrapper.Pa_StartStream(_stream);
	}


    public void Stop()
    {
        _running = false;
		_directionChangeRequested = false;
		_standbyReady = false;
		_fadeFromBuffer = null;
		_fadeSamplesLeft = 0;

        _fillThread?.Join(500);
        PortAudioWrapper.Pa_StopStream(_stream);
        PortAudioWrapper.Pa_CloseStream(_stream);
        PortAudioWrapper.Pa_Terminate();
    }

    public void UpdateDirection()
    {
        if (!_running) return;
        
		_requestedAzimuth = Azimuth;
		_requestedElevation = Elevation;
		_requestedHrirFile = GetNearestHrirFile(_requestedAzimuth, _requestedElevation);
		_directionChangeRequested = true;
    }


	void PrepareStandbyBuffer(int sourcePos)
	{
		var buf = _standbyBuffer;

		Array.Clear(buf.RingL, 0, buf.RingL.Length);
		Array.Clear(buf.RingR, 0, buf.RingR.Length);
		Array.Clear(buf.OverlapL, 0, buf.OverlapL.Length);
		Array.Clear(buf.OverlapR, 0, buf.OverlapR.Length);

		buf.ReadPos = 0;
		buf.WritePos = 0;
		buf.SourcePos = sourcePos;
	}

	void ClearBuffer(AudioBuffer buf, int sourcePos = 0)
	{
		Array.Clear(buf.RingL, 0, buf.RingL.Length);
		Array.Clear(buf.RingR, 0, buf.RingR.Length);
		Array.Clear(buf.OverlapL, 0, buf.OverlapL.Length);
		Array.Clear(buf.OverlapR, 0, buf.OverlapR.Length);

		buf.ReadPos = 0;
		buf.WritePos = 0;
		buf.SourcePos = sourcePos;
	}


    
	void FillRingBuffer()
	{
		AudioBuffer buildBuf = _activeBuffer;
		string buildHrirFile = _currentHrirFile;

		while (_running)
		{
			
			if (_directionChangeRequested)
			{
				// フェード中は old buffer をまだ読んでいる可能性があるので、
				// standby の再利用を少しだけ待つ
				if (_fadeSamplesLeft > 0)
				{
					Thread.Sleep(1);
					continue;
				}

				_directionChangeRequested = false;

				buildBuf = _standbyBuffer;
				buildHrirFile = _requestedHrirFile;

				int startPos = _playbackSamplePos;
				_standbyBaseSamplePos = startPos;
				PrepareStandbyBuffer(startPos);

				_standbyReady = false;

				Console.WriteLine($"Build standby from samplePos={startPos}");
			}



			var buf = buildBuf;

			if (RingBuffered(buf) > SAMPLE_RATE * 2)
			{
				Thread.Sleep(10);
				continue;
			}

			if (buf.SourcePos >= _wChannel.Count)
				break;

			int blockSize = Math.Min(BLOCK_SIZE, _wChannel.Count - buf.SourcePos);

			var blockW = _wChannel.GetRange(buf.SourcePos, blockSize);
			var blockX = _xChannel.GetRange(buf.SourcePos, blockSize);
			var blockY = _yChannel.GetRange(buf.SourcePos, blockSize);
			var blockZ = _zChannel.GetRange(buf.SourcePos, blockSize);

			// ★ここ重要：_currentHrirFile ではなく buildHrirFile を使う
			var (hrirL, hrirR) = _hrirCache[buildHrirFile];

			var convWL = Convolve(blockW, hrirL);
			var convXL = Convolve(blockX, hrirL);
			var convYL = Convolve(blockY, hrirL);
			var convZL = Convolve(blockZ, hrirL);

			var convWR = Convolve(blockW, hrirR);
			var convXR = Convolve(blockX, hrirR);
			var convYR = Convolve(blockY, hrirR);
			var convZR = Convolve(blockZ, hrirR);

			var convL = new List<float>(convWL.Count);
			var convR = new List<float>(convWR.Count);

			for (int i = 0; i < convWL.Count; i++)
			{
				convL.Add(convWL[i] + convXL[i] + convYL[i] + convZL[i]);
				convR.Add(convWR[i] + convXR[i] + convYR[i] + convZR[i]);
			}

			int addLen = Math.Min(buf.OverlapL.Length, convL.Count);
			for (int i = 0; i < addLen; i++)
			{
				convL[i] += buf.OverlapL[i];
				convR[i] += buf.OverlapR[i];
			}

			for (int i = 0; i < blockSize; i++)
			{
				buf.RingL[buf.WritePos] = convL[i];
				buf.RingR[buf.WritePos] = convR[i];
				buf.WritePos = (buf.WritePos + 1) % RING_SIZE;
			}

			int overlapSize = convL.Count - blockSize;

			Array.Clear(buf.OverlapL, 0, buf.OverlapL.Length);
			Array.Clear(buf.OverlapR, 0, buf.OverlapR.Length);

			Array.Copy(convL.ToArray(), blockSize, buf.OverlapL, 0, Math.Min(overlapSize, 511));
			Array.Copy(convR.ToArray(), blockSize, buf.OverlapR, 0, Math.Min(overlapSize, 511));

			buf.SourcePos += blockSize;

			// ★ standby が十分できたか判定
			if (ReferenceEquals(buf, _standbyBuffer))
			{
				int delta = _playbackSamplePos - _standbyBaseSamplePos;
				if (delta < 0) delta = 0;

				int availableAhead = RingBuffered(buf) - delta;

				if (availableAhead >= SWAP_PREROLL)
				{
					_standbyReady = true;
				}
			}
		}
	}


    
	int Callback(IntPtr input, IntPtr output, ulong frameCount, IntPtr timeInfo, uint statusFlags, IntPtr userData)
	{
		
		if (_standbyReady)
		{
			var standby = _standbyBuffer;

			int delta = _playbackSamplePos - _standbyBaseSamplePos;
			if (delta < 0) delta = 0;

			int availableAhead = RingBuffered(standby) - delta;

			// standby が現在位置に追いついたうえで十分残量があるときだけ swap
			if (availableAhead >= (int)frameCount)
			{
				standby.ReadPos = delta % RING_SIZE;

				var old = _activeBuffer;

				// ★ フェード元として old active を保存
				_fadeFromBuffer = old;
				_fadeSamplesLeft = CROSSFADE_SAMPLES;

				_activeBuffer = standby;
				_standbyBuffer = old;

				_currentHrirFile = _requestedHrirFile;
				_standbyReady = false;

				Console.WriteLine("Swap active <-> standby (crossfade start)");
			}
		}


		var active = _activeBuffer;

		if (RingBuffered(active) < (int)frameCount)
		{
			for (int i = 0; i < (int)frameCount; i++)
			{
				Marshal.WriteInt32(output, i * 8 + 0, 0);
				Marshal.WriteInt32(output, i * 8 + 4, 0);
			}
			return 0;
		}

		
		for (int i = 0; i < (int)frameCount; i++)
		{
			// 新しい active 側のサンプルを読む
			float newL = active.RingL[active.ReadPos];
			float newR = active.RingR[active.ReadPos];
			active.ReadPos = (active.ReadPos + 1) % RING_SIZE;

			float outL = newL;
			float outR = newR;

			// フェード中なら old buffer と混ぜる
			if (_fadeSamplesLeft > 0 && _fadeFromBuffer != null)
			{
				var fadeBuf = _fadeFromBuffer;

				if (RingBuffered(fadeBuf) > 0)
				{
					float oldL = fadeBuf.RingL[fadeBuf.ReadPos];
					float oldR = fadeBuf.RingR[fadeBuf.ReadPos];
					fadeBuf.ReadPos = (fadeBuf.ReadPos + 1) % RING_SIZE;

					float fadeIn = 1f - (_fadeSamplesLeft / (float)CROSSFADE_SAMPLES);
					float fadeOut = 1f - fadeIn;

					outL = oldL * fadeOut + newL * fadeIn;
					outR = oldR * fadeOut + newR * fadeIn;

					_fadeSamplesLeft--;

					if (_fadeSamplesLeft == 0)
					{
						_fadeFromBuffer = null;
					}
				}
				else
				{
					// old buffer が尽きたらフェード終了
					_fadeSamplesLeft = 0;
					_fadeFromBuffer = null;
				}
			}

			Marshal.WriteInt32(output, i * 8 + 0, BitConverter.SingleToInt32Bits(outL));
			Marshal.WriteInt32(output, i * 8 + 4, BitConverter.SingleToInt32Bits(outR));

			_playbackSamplePos++;
		}


		return 0;
	}


    string GetNearestHrirFile(float azimuth, float elevation)
    {
        var files = Directory.GetFiles(HrirDir, "*.wav");
        string nearest = files[0];
        float minDist = float.MaxValue;
        foreach (var file in files)
        {
            var (azi, ele) = ParseHrirFileName(file);
            float dist = MathF.Sqrt(MathF.Pow(azi - azimuth, 2) + MathF.Pow(ele - elevation, 2));
            if (dist < minDist) { minDist = dist; nearest = file; }
        }
        return nearest;
    }

    (float azimuth, float elevation) ParseHrirFileName(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        var parts = fileName.Split('_');
        float azimuth = Convert.ToSingle(parts[1].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        float elevation = Convert.ToSingle(parts[3].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        return (azimuth, elevation);
    }

    (List<float> L, List<float> R) ReadHrirFile(string filePath)
    {
        var l = new List<float>();
        var r = new List<float>();
        using var reader = new BinaryReader(File.OpenRead(filePath));
        reader.ReadBytes(4);
        reader.ReadBytes(4);
        reader.ReadBytes(4);
        short channels = 0;
        short bitsPerSample = 0;
        uint dataSize = 0;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            uint chunkSize = reader.ReadUInt32();
            if (chunkId == "fmt ")
            {
                reader.ReadBytes(2);
                channels = reader.ReadInt16();
                reader.ReadInt32();
                reader.ReadBytes(4);
                reader.ReadBytes(2);
                bitsPerSample = reader.ReadInt16();
                int remaining = (int)chunkSize - 16;
                if (remaining > 0) reader.ReadBytes(remaining);
                if (chunkSize % 2 != 0) reader.ReadByte();
            }
            else if (chunkId == "data")
            {
                dataSize = chunkSize;
                int totalFrames = (int)(dataSize / (channels * (bitsPerSample / 8)));
                for (int i = 0; i < totalFrames; i++)
                {
                    float[] frame = new float[channels];
                    for (int ch = 0; ch < channels; ch++)
                    {
                        byte[] buf = reader.ReadBytes(3);
                        int rawInt = buf[0] | (buf[1] << 8) | (buf[2] << 16);
                        if ((rawInt & 0x800000) != 0)
                            rawInt |= unchecked((int)0xFF000000);
                        frame[ch] = rawInt / 8388607f;
                    }
                    l.Add(frame[0]);
                    r.Add(frame[1]);
                }
                break;
            }
            else
            {
                reader.ReadBytes((int)chunkSize);
                if (chunkSize % 2 != 0) reader.ReadByte();
            }
        }
        return (l, r);
    }

    static List<float> Convolve(List<float> signal, List<float> ir)
    {
        int outLen = signal.Count + ir.Count - 1;
        int fftSize = 1;
        while (fftSize < outLen) fftSize <<= 1;
        var sigC = new Complex32[fftSize];
        var irC = new Complex32[fftSize];
        for (int i = 0; i < signal.Count; i++) sigC[i] = new Complex32(signal[i], 0);
        for (int i = 0; i < ir.Count; i++) irC[i] = new Complex32(ir[i], 0);
        Fourier.Forward(sigC, FourierOptions.AsymmetricScaling);
        Fourier.Forward(irC, FourierOptions.AsymmetricScaling);
        for (int i = 0; i < fftSize; i++) sigC[i] *= irC[i];
        Fourier.Inverse(sigC, FourierOptions.AsymmetricScaling);
        var output = new List<float>(outLen);
        for (int i = 0; i < outLen; i++) output.Add(sigC[i].Real);
        return output;
    }
}