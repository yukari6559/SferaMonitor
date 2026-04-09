using System.Runtime.InteropServices;
using PaError = System.Int32;
using PaDeviceIndex = System.Int32;

public class Program
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
	public static void Main()
	{
		Pa_Initialize();
		Console.WriteLine("デバイス一覧:");
		int i = Pa_GetDeviceCount();
		for(int j = 0; j < i; j++)
		{
			IntPtr ptr = Pa_GetDeviceInfo(j);
			var info = Marshal.PtrToStructure<PaDeviceInfo>(ptr); 
			string name = Marshal.PtrToStringAnsi(info.Name);
			int outChannelCount = info.MaxOutputChannels;
			int inChannelCount = info.MaxInputChannels;

			Console.WriteLine($"  [{j}] {name} (in:{inChannelCount} out:{outChannelCount})");
		}
		int DefaultInDeviceIndex = Pa_GetDefaultInputDevice();
		int DefaultOutDeviceIndex = Pa_GetDefaultOutputDevice();
		IntPtr DefaultInDeviceInfoPtr = Pa_GetDeviceInfo(DefaultInDeviceIndex);
		IntPtr DefaultOutDeviceInfoPtr = Pa_GetDeviceInfo(DefaultOutDeviceIndex);
		var DefaultInDeviceInfo = Marshal.PtrToStructure<PaDeviceInfo>(DefaultInDeviceInfoPtr);
		var DefaultOutDeviceInfo = Marshal.PtrToStructure<PaDeviceInfo>(DefaultOutDeviceInfoPtr);
		string DefaultInDeviceName = Marshal.PtrToStringAnsi(DefaultInDeviceInfo.Name);
		string DefaultOutDeviceName = Marshal.PtrToStringAnsi(DefaultOutDeviceInfo.Name);
		Console.WriteLine($"デフォルト入力: [{DefaultInDeviceIndex}] {DefaultInDeviceName}");
		Console.WriteLine($"デフォルト出力: [{DefaultOutDeviceIndex}] {DefaultOutDeviceName}");

	}
}