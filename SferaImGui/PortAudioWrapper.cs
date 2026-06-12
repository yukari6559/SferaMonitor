using System.Runtime.InteropServices;
using PaError = System.Int32;
using PaDeviceIndex = System.Int32;

public class PortAudioWrapper
{
	[DllImport("libportaudio.dylib")]
	public static extern PaError Pa_Initialize();

	[DllImport("libportaudio.dylib")]
	public static extern PaDeviceIndex Pa_GetDeviceCount();

	[DllImport("libportaudio.dylib")]
	public static extern IntPtr Pa_GetDeviceInfo(int device);

	[DllImport("libportaudio.dylib")]
	public static extern PaDeviceIndex Pa_GetDefaultInputDevice();

	[DllImport("libportaudio.dylib")]
	public static extern PaDeviceIndex Pa_GetDefaultOutputDevice();

	[DllImport("libportaudio.dylib")]
	public static extern PaError Pa_Terminate();

	[DllImport("libportaudio.dylib")]
	public static extern PaError Pa_OpenDefaultStream(out IntPtr stream, int numInputChannels, int numOutputChannels, uint sampleFormat, double sampleRate, ulong framesPerBuffer, IntPtr streamCallback, IntPtr userdata);

	[DllImport("libportaudio.dylib")]
	public static extern int Pa_StartStream(IntPtr stream);

	[DllImport("libportaudio.dylib")]
	public static extern int Pa_StopStream(IntPtr stream);

	[DllImport("libportaudio.dylib")]
	public static extern int Pa_CloseStream(IntPtr stream);

	[DllImport("libportaudio.dylib")]
	public static extern int Pa_Sleep(long msec);


	[StructLayout(LayoutKind.Sequential)]
	public struct PaDeviceInfo
	{
		public int StructVersion;
		public IntPtr Name;
		public int HostApi;
		public int MaxInputChannels;
		public int MaxOutputChannels;
		public double DefaultLowInputLatency;
		public double DefaultOutputLatency;
		public double DefaultHighInputLatency;
		public double DefaultHighOutputLatency;
		public double DefaultSampleRate;
	}

	public delegate int Pa_StreamCallbackDelegate(IntPtr input, IntPtr output, ulong frameCount, IntPtr timeInfo, uint statusFlags, IntPtr userdata);
}