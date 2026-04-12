using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public class Program
{
	private static IntPtr stream;
	private static List<float> _wChannel = new List<float>();
	private static List<float> _xChannel = new List<float>();
	private static List<float> _yChannel = new List<float>();
	private static List<float> _hrir_L = new List<float>();
	private static List<float> _hrir_R = new List<float>();


	static int _playbackPos = 0;
	
	public static void Main()
    {
        using var reader = new BinaryReader(File.OpenRead("/Users/mths40035/Downloads/D2/D2_HRIR_WAV/96K_24bit/azi_353,0_ele_-64,8.wav"));

        // RIFFヘッダー
        reader.ReadBytes(4); // "RIFF"
        reader.ReadBytes(4); // ChunkSize
        reader.ReadBytes(4); // "WAVE"

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        uint dataSize = 0;

        // チャンクをループで読む
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

                // fmtチャンクに余りがあればスキップ（拡張fmtの場合）
                int remaining = (int)chunkSize - 16;
                if (remaining > 0) reader.ReadBytes(remaining);
				if (chunkSize % 2 != 0)
        			reader.ReadByte();
            }
            else if (chunkId == "data")
			{
				dataSize = chunkSize;
				int totalFrames = (int)(dataSize / (channels * (bitsPerSample / 8)));

				for (int i = 0; i < totalFrames; i++) // 全フレーム
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
					_hrir_L.Add(frame[0]);
					_hrir_R.Add(frame[1]);

				}
				break;
			}
            else
            {
                // bext, REAPER, axml など全部スキップ
                reader.ReadBytes((int)chunkSize);
				if (chunkSize % 2 != 0)
        			reader.ReadByte();
            }
        }

        // 再生時間の計算
        long durationMs = (long)(_wChannel.Count / (double)sampleRate * 1000);

        Console.WriteLine($"チャンネル数: {channels}");
        Console.WriteLine($"サンプルレート: {sampleRate} Hz");
        Console.WriteLine($"ビット深度: {bitsPerSample} bit");
        Console.WriteLine($"再生時間: {durationMs:hh\\:mm\\:ss}");

		Console.WriteLine($"IRの長さ: {_hrir_L.Count} サンプル");
		Console.WriteLine($"L[0]: {_hrir_L[0]}  R[0]: {_hrir_R[0]}");
    }
}