using Vortice.ShaderCompiler;

// Bakes the renderer's GLSL 450 shaders (Shaders/*.vert, Shaders/*.frag) to a target format at
// build time, so VkPipelineSet loads pre-compiled bytecode instead of invoking shaderc at runtime.
// Runtime shaderc (Silk.NET.Shaderc.Native via Vortice.ShaderCompiler) ships NO android RID, so the
// runtime path was unloadable on Android; baking here removes shaderc from the shipped package
// entirely (also trims AOT surface + first-renderer startup cost).
//
// SPIR-V is the hub: author GLSL once -> compile to SPIR-V -> (future) per-target back-end. Only the
// SpirV target is implemented today; GlslEs (via SPIRV-Cross, for WebGl.Renderer) and Wgsl (via
// naga/Tint, for a browser-WebGPU renderer) are reserved for when those consumers exist.
//
// usage: BakeShaders <shaderDir> [--target spirv|glsles|wgsl]
//   <shaderDir>  directory holding *.vert / *.frag; output goes to <shaderDir>/<target>/
//   --target     output format (default: spirv)

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: BakeShaders <shaderDir> [--target spirv|glsles|wgsl]");
    return 1;
}

var shaderDir = Path.GetFullPath(args[0]);
var target = "spirv";
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--target" && i + 1 < args.Length)
        target = args[++i].ToLowerInvariant();
    else
    {
        Console.Error.WriteLine($"unknown argument: {args[i]}");
        return 1;
    }
}

if (!Directory.Exists(shaderDir))
{
    Console.Error.WriteLine($"shader directory not found: {shaderDir}");
    return 1;
}

if (target is "glsles" or "wgsl")
{
    // SPIR-V -> GLSL ES (SPIRV-Cross) / WGSL (naga|Tint) back-ends land with the WebGL/WebGPU
    // renderers. Fail loudly rather than silently emitting nothing.
    Console.Error.WriteLine($"--target {target} is not implemented yet (SPIR-V hub is in place; back-end deferred).");
    return 2;
}

if (target != "spirv")
{
    Console.Error.WriteLine($"unknown target: {target} (expected spirv|glsles|wgsl)");
    return 1;
}

var outDir = Path.Combine(shaderDir, "spirv");
Directory.CreateDirectory(outDir);

// *.vert -> VertexShader, *.frag -> FragmentShader. Ordered for a stable, reviewable build log.
var sources = Directory.GetFiles(shaderDir, "*.vert")
    .Concat(Directory.GetFiles(shaderDir, "*.frag"))
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToArray();

if (sources.Length == 0)
{
    Console.Error.WriteLine($"no *.vert / *.frag shaders found in {shaderDir}");
    return 1;
}

using var compiler = new Compiler();
var baked = 0;
foreach (var src in sources)
{
    var fileName = Path.GetFileName(src);
    var kind = Path.GetExtension(src) == ".vert" ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
    var source = File.ReadAllText(src);

    // Shader source must be ASCII: shaderc's lexer chokes on non-ASCII bytes even inside //
    // comments (a stray em dash reports as a cryptic "unexpected end of file"). Point at it clearly.
    var badLine = source.Split('\n').Select((line, n) => (line, n)).FirstOrDefault(t => t.line.Any(c => c > 0x7F));
    if (badLine.line is not null)
        Console.Error.WriteLine($"warning: {fileName}({badLine.n + 1}) contains non-ASCII characters; shaderc may reject it. Use ASCII (e.g. '--' for em dashes).");

    // Vulkan_1_0 target env matches the baseline VkPipelineSet compiled against and keeps the
    // bytecode valid on the widest device range (incl. Mali baseline on Android).
    var options = new CompilerOptions
    {
        TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
        ShaderStage = kind
    };
    var result = compiler.Compile(source, fileName, options);
    if (result.Status != CompilationStatus.Success)
    {
        Console.Error.WriteLine($"shader compilation failed ({fileName}): {result.ErrorMessage}");
        return 3;
    }

    var outPath = Path.Combine(outDir, fileName + ".spv");
    File.WriteAllBytes(outPath, result.Bytecode);
    Console.WriteLine($"{fileName} -> {Path.GetFileName(outPath)} ({result.Bytecode.Length} bytes)");
    baked++;
}

Console.WriteLine($"baked {baked} shader(s) [{target}] -> {outDir}");
return 0;
