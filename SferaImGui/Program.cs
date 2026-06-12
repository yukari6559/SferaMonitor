using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using ImGuiNET;
using Silk.NET.Input;
using System.Runtime.InteropServices;



var options = WindowOptions.Default with
{
    Title = "Sfera",
    Size = new Silk.NET.Maths.Vector2D<int>(480, 320)
};

var window = Window.Create(options);
GL gl = null!;
ImGuiController controller = null!;
var pathBuf = new byte[512]; // ← ここに追加
var engine = new SferaEngine();
float lastAzimuth = 0f;
float lastElevation = 0f;


window.Load += () =>
{
    gl = window.CreateOpenGL();
    controller = new ImGuiController(gl, window, window.CreateInput());

	var io = ImGui.GetIO();

	io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
	
};

window.Render += delta =>  // asyncを削除
{
    controller.Update((float)delta);
    gl.ClearColor(0.1f, 0.1f, 0.2f, 1f);
    gl.Clear(ClearBufferMask.ColorBufferBit);

    ImGui.SetNextWindowSize(new System.Numerics.Vector2(460, 300));
    ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10));
    ImGui.Begin("Sfera", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);

    if (ImGui.Button("Load"))
    {
        var scriptPath = Path.GetTempFileName() + ".scpt";
        File.WriteAllText(scriptPath, "choose file of type {\"wav\"}\nreturn POSIX path of result");
        var p = new System.Diagnostics.Process();
        p.StartInfo = new System.Diagnostics.ProcessStartInfo("osascript")
        {
            Arguments = scriptPath,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        p.Start();
        var path = p.StandardOutput.ReadToEnd().Trim();
        File.Delete(scriptPath);
        if (!string.IsNullOrEmpty(path))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(path);
            Array.Clear(pathBuf, 0, pathBuf.Length);
            Array.Copy(bytes, pathBuf, Math.Min(bytes.Length, pathBuf.Length - 1));
            Task.Run(() => engine.LoadWav(path)); // awaitしない
        }
    }

    ImGui.SameLine();
    ImGui.InputText("##path", pathBuf, (uint)pathBuf.Length);

    ImGui.Spacing();

    ImGui.SliderFloat("Azimuth", ref engine.Azimuth, 0f, 360f);
    ImGui.SliderFloat("Elevation", ref engine.Elevation, -90f, 90f);
	if (engine.Azimuth != lastAzimuth || engine.Elevation != lastElevation)
	{
		lastAzimuth = engine.Azimuth;
		lastElevation = engine.Elevation;
		if (engine.IsPlaying)
			Task.Run(() => engine.UpdateDirection());
	}


    ImGui.Spacing();

    if (ImGui.Button("Play")) Task.Run(() => engine.Play());
    ImGui.SameLine();
    if (ImGui.Button("Stop")) engine.Stop();

    ImGui.End();
    controller.Render();
};

window.Closing += () => controller.Dispose();
window.Run();