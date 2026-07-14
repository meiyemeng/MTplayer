using System.Text;
using System.Text.Json;
using System.Globalization;
using Python.Runtime;

var pythonDll = ResolvePythonDll();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
Runtime.PythonDLL = pythonDll;
PythonEngine.Initialize();

try
{
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        try
        {
            var request = JsonSerializer.Deserialize<PythonSpiderRequest>(line, jsonOptions) ?? throw new InvalidDataException("请求为空。");
            var responseJson = Execute(request);
            Console.WriteLine(JsonSerializer.Serialize(new PythonSpiderResponse(responseJson, null)));
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new PythonSpiderResponse(null, exception.Message)));
        }
    }
}
finally
{
    PythonEngine.Shutdown();
}

static string Execute(PythonSpiderRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Method)) throw new ArgumentException("缺少 Spider 方法。", nameof(request));
    var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Script));
    var argumentsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Arguments.GetRawText()));

    using (Py.GIL())
    using (var scope = Py.CreateScope())
    {
        scope.Set("script_b64", scriptBase64.ToPython());
        scope.Set("arguments_b64", argumentsBase64.ToPython());
        scope.Set("method_name", request.Method.ToPython());
        scope.Exec("""
            import base64
            import json
            exec(base64.b64decode(script_b64).decode('utf-8'), globals())
            result = globals()[method_name](*json.loads(base64.b64decode(arguments_b64).decode('utf-8')))
            response_json = json.dumps(result, ensure_ascii=False)
            """);
        return scope.Get("response_json").ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException("Spider 未返回 JSON 结果。");
    }
}

static string ResolvePythonDll()
{
    var candidates = new[]
    {
        Environment.GetEnvironmentVariable("WEBHTV_PYTHON_DLL"),
        Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python312.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python312.dll")
    };
    return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        ?? throw new FileNotFoundException("未找到 Python 3.12 运行时。", string.Join(";", candidates));
}

internal sealed record PythonSpiderRequest(string Script, string Method, JsonElement Arguments);
internal sealed record PythonSpiderResponse(string? ResultJson, string? Error);
