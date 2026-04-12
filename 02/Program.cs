using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

public class Program
{
	const int RING_SECONDS = 4;          // 何秒分溜めるか
	const int SAMPLE_RATE = 96000;
	const int RING_SIZE = RING_SECONDS * SAMPLE_RATE;

    private static IntPtr stream;
    private static List<float> _wChannel = new List<float>();
    private static List<float> _xChannel = new List<float>();
    private static List<float> _yChannel = new List<float>();
    private static Dictionary<string, (List<float> L, List<float> R)> _hrirCache = new();

    static float _azimuth = 0f;
    static float _elevation = 0f;
    static string _hrirDir = "/Users/mths40035/Downloads/D2/D2_HRIR_WAV/96K_24bit";
    static string _currentHrirFile = "";
	static float[] _ringL = new float[RING_SIZE];
	static float[] _ringR = new float[RING_SIZE];
	static int _ringReadPos  = 0; // コールバックが読む位置
	static int _ringWritePos = 0; // バックグラウンドが書く位置
	static float[] _fillOverlapL = new float[511];
	static float[] _fillOverlapR = new float[511];
	static int BLOCK_SIZE = 4096; // バックグラウンドで一度に処理するフレーム数
	static int _playbackSamplePos = 0; // 元音声の再生位置（コールバックが進める）


    
    static int _playbackPos = 0;

	static int RingBuffered() => (_ringWritePos - _ringReadPos + RING_SIZE) % RING_SIZE;

	static int _sourcePos = 0; // 元音声のどこまで処理したか
	static bool _running = true;

	static void ResetBuffer()
	{
		_running = false;
		Thread.Sleep(50); // スレッドが止まるのを待つ
		
		_sourcePos = _playbackSamplePos; // ← 元音声の位置を使う
		_ringWritePos = _ringReadPos;
		Array.Clear(_fillOverlapL, 0, _fillOverlapL.Length);
		Array.Clear(_fillOverlapR, 0, _fillOverlapR.Length);
		
		_running = true;
		var fillThread = new Thread(FillRingBuffer);
		fillThread.IsBackground = true;
		fillThread.Start();
	}

	static void FillRingBuffer()
	{
		while (_running)
		{
			if (RingBuffered() > SAMPLE_RATE * 2)
			{
				Thread.Sleep(10);
				continue;
			}

			if (_sourcePos >= _wChannel.Count) break;

			int blockSize = Math.Min(BLOCK_SIZE, _wChannel.Count - _sourcePos);
			var block = _wChannel.GetRange(_sourcePos, blockSize);

			var (hrirL, hrirR) = _hrirCache[_currentHrirFile];
			var convL = Convolve(block, hrirL);
			var convR = Convolve(block, hrirR);

			// 持ち越し分を足し合わせる
			int addLen = Math.Min(_fillOverlapL.Length, convL.Count);
			for (int i = 0; i < addLen; i++)
			{
				convL[i] += _fillOverlapL[i];
				convR[i] += _fillOverlapR[i];
			}

			// リングバッファに書き込む
			for (int i = 0; i < blockSize; i++)
			{
				_ringL[_ringWritePos] = convL[i];
				_ringR[_ringWritePos] = convR[i];
				_ringWritePos = (_ringWritePos + 1) % RING_SIZE;
			}

			// 余りを次回に持ち越す
			int overlapSize = convL.Count - blockSize;
			Array.Clear(_fillOverlapL, 0, _fillOverlapL.Length);
			Array.Clear(_fillOverlapR, 0, _fillOverlapR.Length);
			Array.Copy(convL.ToArray(), blockSize, _fillOverlapL, 0, Math.Min(overlapSize, 511));
			Array.Copy(convR.ToArray(), blockSize, _fillOverlapR, 0, Math.Min(overlapSize, 511));

			_sourcePos += blockSize;
		}
	}
	static List<float> Convolve(List<float> signal, List<float> ir)
	{
		int outLen = signal.Count + ir.Count - 1;
		int fftSize = 1;
		while (fftSize < outLen) fftSize <<= 1;

		var sigC = new Complex32[fftSize];
		var irC  = new Complex32[fftSize];
		for (int i = 0; i < signal.Count; i++) sigC[i] = new Complex32(signal[i], 0);
		for (int i = 0; i < ir.Count; i++)     irC[i]  = new Complex32(ir[i], 0);

		Fourier.Forward(sigC, FourierOptions.AsymmetricScaling);
		Fourier.Forward(irC,  FourierOptions.AsymmetricScaling);
		for (int i = 0; i < fftSize; i++) sigC[i] *= irC[i];
		Fourier.Inverse(sigC, FourierOptions.AsymmetricScaling);

		var output = new List<float>(outLen);
		for (int i = 0; i < outLen; i++) output.Add(sigC[i].Real);
		return output;
	}
    static (List<float> L, List<float> R) ReadHrirFile(string filePath)
    {
        var l = new List<float>();
        var r = new List<float>();

        using (var reader = new BinaryReader(File.OpenRead(filePath)))
        {
			reader.ReadBytes(4); // "RIFF"
			reader.ReadBytes(4); // ChunkSize
			reader.ReadBytes(4); // "WAVE"
            short channels = 0;
            int sampleRate = 0;
            short bitsPerSample = 0;
            uint dataSize = 0;
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                uint chunkSize = reader.ReadUInt32();

                if (chunkId == "fmt ")
                {
                    reader.ReadBytes(2); // FormatTag
                    channels      = reader.ReadInt16();
                    sampleRate    = reader.ReadInt32();
                    reader.ReadBytes(4); // AvgBytesPerSec
                    reader.ReadBytes(2); // BlockAlign
                    bitsPerSample = reader.ReadInt16();

                    int remaining = (int)chunkSize - 16;
                    if (remaining > 0) reader.ReadBytes(remaining);
                    if (chunkSize % 2 != 0)
                        reader.ReadByte();
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
                    if (chunkSize % 2 != 0)
                        reader.ReadByte();
                }
            }
        }

        return (l, r);
    }

    static void LoadAllHrirs(string hrirDir)
    {
        var files = Directory.GetFiles(hrirDir, "*.wav");
        foreach (var file in files)
        {
            var (l, r) = ReadHrirFile(file); 
            _hrirCache[file] = (l, r);
        }
        Console.WriteLine($"HRIR {_hrirCache.Count}件読み込み完了");
    }

    // static int Callback(IntPtr input, IntPtr output, ulong frameCount, IntPtr timeInfo, uint statusFlags, IntPtr userData)
    // {
	// 	Console.WriteLine("");
    //     // HRIRが未設定、または音声データが無い場合は無音を出力
    //     if (string.IsNullOrEmpty(_currentHrirFile) || !_hrirCache.ContainsKey(_currentHrirFile) || _wChannel.Count == 0)
    //     {
    //         for (int i = 0; i < (int)frameCount; i++)
    //         {
    //             Marshal.WriteInt32(output, i * 8 + 0, 0); // 0.0f のビット表現は 0
    //             Marshal.WriteInt32(output, i * 8 + 4, 0);
    //         }
    //         return 0; 
    //     }

    //     var hrir = _hrirCache[_currentHrirFile];
    //     int irLen = hrir.L.Count;

    //     for (int i = 0; i < (int)frameCount; i++)
    //     {
    //         if (_playbackPos >= _wChannel.Count)
    //         {
    //             return 1; // 再生終了
    //         }

    //         float outL = 0f;
    //         float outR = 0f;

    //         // 時間領域のリアルタイム畳み込み
    //         for (int k = 0; k < irLen; k++)
    //         {
    //             int sampleIdx = _playbackPos - k;
    //             if (sampleIdx >= 0 && sampleIdx < _wChannel.Count)
    //             {
    //                 outL += _wChannel[sampleIdx] * hrir.L[k];
    //                 outR += _wChannel[sampleIdx] * hrir.R[k];
    //             }
    //         }

    //         Marshal.WriteInt32(output, i * 8 + 0, BitConverter.SingleToInt32Bits(outL));
    //         Marshal.WriteInt32(output, i * 8 + 4, BitConverter.SingleToInt32Bits(outR));

    //         _playbackPos++;
    //     }

    //     return 0;
    // }

	static float[] _precomputedL = Array.Empty<float>();
	static float[] _precomputedR = Array.Empty<float>();
	static int _readPos = 0;
	static bool _bufferReady = false;
	static int Callback(IntPtr input, IntPtr output, ulong frameCount, IntPtr timeInfo, uint statusFlags, IntPtr userData)
	{
		int buffered = RingBuffered();

		// バッファが足りない場合は無音
		if (buffered < (int)frameCount)
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
			Marshal.WriteInt32(output, i * 8 + 0,
				BitConverter.SingleToInt32Bits(_ringL[_ringReadPos]));
			Marshal.WriteInt32(output, i * 8 + 4,
				BitConverter.SingleToInt32Bits(_ringR[_ringReadPos]));
			_ringReadPos = (_ringReadPos + 1) % RING_SIZE;
			_playbackSamplePos++;
		}
		return 0;
	}

    static string GetNearestHrirFile(string hrirDir, float azimuth, float elevation)
    {
        var files = Directory.GetFiles(hrirDir, "*.wav");
        
        string nearest = files[0];
        float minDist = float.MaxValue;

        foreach (var file in files)
        {
            var (azi, ele) = ParseHrirFileName(file);
            
            float dist = MathF.Sqrt(MathF.Pow(azi - azimuth, 2) + MathF.Pow(ele - elevation, 2));
            
            if (dist < minDist)
            {
                minDist = dist;
                nearest = file;
            }
        }
        return nearest;
    }

    static (float azimuth, float elevation) ParseHrirFileName(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        var parts = fileName.Split('_');

        float azimuth   = Convert.ToSingle(parts[1].Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);
        float elevation = Convert.ToSingle(parts[3].Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);
        return (azimuth, elevation);
    }
    
    public static void Main()
    {
        using (var reader = new BinaryReader(File.OpenRead("/Users/mths40035/Downloads/N016af31448123f639272.wav")))
        {
            reader.ReadBytes(4); // "RIFF"
            reader.ReadBytes(4); // ChunkSize
            reader.ReadBytes(4); // "WAVE"

            short channels = 0;
            int sampleRate = 0;
            short bitsPerSample = 0;
            uint dataSize = 0;

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                uint chunkSize = reader.ReadUInt32();

                if (chunkId == "fmt ")
                {
                    reader.ReadBytes(2); // FormatTag
                    channels      = reader.ReadInt16();
                    sampleRate    = reader.ReadInt32();
                    reader.ReadBytes(4); // AvgBytesPerSec
                    reader.ReadBytes(2); // BlockAlign
                    bitsPerSample = reader.ReadInt16();

                    int remaining = (int)chunkSize - 16;
                    if (remaining > 0) reader.ReadBytes(remaining);
                    if (chunkSize % 2 != 0)
                        reader.ReadByte();
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
                    }
                    break;
                }
                else
                {
                    reader.ReadBytes((int)chunkSize);
                    if (chunkSize % 2 != 0)
                        reader.ReadByte();
                }
            }
        }

        // HRIRのロード
        LoadAllHrirs(_hrirDir);

        PortAudioWrapper.Pa_Initialize();
        PortAudioWrapper.Pa_StreamCallbackDelegate callback = new PortAudioWrapper.Pa_StreamCallbackDelegate(Callback);
        var callbackPtr = Marshal.GetFunctionPointerForDelegate(callback);
        
        int err = PortAudioWrapper.Pa_OpenDefaultStream(out stream, 0, 2, 0x00000001, 96000, 256, callbackPtr, IntPtr.Zero);
        Console.WriteLine($"OpenDefaultStream: {err}");
        
        // 初期の方向を設定
        _currentHrirFile = GetNearestHrirFile(_hrirDir, _azimuth, _elevation);
		Console.WriteLine(_currentHrirFile);

		Task.Run(() =>
		{
			var (hrirL, hrirR) = _hrirCache[_currentHrirFile];
			Console.WriteLine($"wChannel: {_wChannel.Count}  hrirL: {hrirL.Count}");
			_precomputedL = Convolve(_wChannel, hrirL).ToArray();
			_precomputedR = Convolve(_wChannel, hrirR).ToArray();
			_readPos = 0;
			float maxVal = _precomputedL.Max(MathF.Abs);
			Console.WriteLine($"最大振幅: {maxVal}");
			_bufferReady = true;
			Console.WriteLine("計算完了");
		});

		var fillThread = new Thread(FillRingBuffer)
		{
			IsBackground = true
		};
		fillThread.Start();

        PortAudioWrapper.Pa_StartStream(stream);
        Console.WriteLine("再生開始 - 十字キーで方向を変更、Qで終了");

        while (true)
		{
			var key = Console.ReadKey(intercept: true).Key;
			if (key == ConsoleKey.Q) break;

			bool changed = false;
			if (key == ConsoleKey.RightArrow) { _azimuth = (_azimuth + 10) % 360;              changed = true; }
			if (key == ConsoleKey.LeftArrow)  { _azimuth = (_azimuth - 10 + 360) % 360;        changed = true; }
			if (key == ConsoleKey.UpArrow)    { _elevation = Math.Clamp(_elevation + 10, -90, 90); changed = true; }
			if (key == ConsoleKey.DownArrow)  { _elevation = Math.Clamp(_elevation - 10, -90, 90); changed = true; }

			if (changed)
			{
				_currentHrirFile = GetNearestHrirFile(_hrirDir, _azimuth, _elevation);
				Console.WriteLine($"az:{_azimuth} el:{_elevation}");
				ResetBuffer();
			}
		}

        // Qを押してループを抜けたらストリームを閉じて終了
        PortAudioWrapper.Pa_StopStream(stream);
        PortAudioWrapper.Pa_CloseStream(stream);
        PortAudioWrapper.Pa_Terminate();
    }
}