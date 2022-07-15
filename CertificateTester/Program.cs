// Small utility app to validate migration scenarios between .NET 6.0 and .NET 7.0
using Microsoft.AspNetCore.Certificates.Generation;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

using var client = new HttpClient();

// We will a modified version of the certificate manager to setup
// the different initial scenarios between .NET 6.0 and .NET 7.0
// The tool will invoke dotnet dev-certs in several situations as well
// as run a .NET 7.0 app to validate the user experience (only a log message will be displayed when
// it can't access the HTTPS certificate).
var manager = CertificateManager.Instance;

// Clean the state on the machine
Console.WriteLine("Setting up initial scenario, existing, valid .NET 6.0 certificate (untrusted)");
Console.WriteLine("Removing all the certificates.");
manager.RemoveAllCertificates(StoreName.My, StoreLocation.CurrentUser);
manager.RemoveAllCertificates(StoreName.Root, StoreLocation.CurrentUser);

Console.WriteLine("Creating a new certificate with dotnet dev-certs https.");
await RunDotnetApp(
    "dotnet",
    "dev-certs https",
    Path.GetFullPath(Environment.CurrentDirectory, "../../net60sdk"));

Console.WriteLine("Running a .NET 7.0 web application.");
var net7App = RunDotnetApp(
    "dotnet",
    "run",
    Path.GetFullPath(Environment.CurrentDirectory,"../../../aspnetcore/.dotnet"),
    "../../webapp7");

Console.WriteLine("Requesting https://localhost:5001/");

Console.WriteLine(await client.GetStringAsync("https://localhost:5001"));

await net7App;

// Login keychain
DisplayCertificates(StoreName.My, StoreLocation.CurrentUser);

// Login keychain but only trusted (we don't use this, just displaying for completeness).
DisplayCertificates(StoreName.Root, StoreLocation.CurrentUser);

// System keychain
DisplayCertificates(StoreName.Root, StoreLocation.LocalMachine);


static void DisplayCertificates(StoreName name, StoreLocation location)
{
    using var store = new X509Store(name, location);
    store.Open(OpenFlags.ReadOnly);
    CertificateManager.ToCertificateDescription(store.Certificates.Where(c => CertificateManager.HasOid(c, CertificateManager.AspNetHttpsOid)));
    store.Close();
}

async Task RunDotnetApp(string appName, string args, string sdkPath, string? workingDir = null)
{
    await RunProcessAndDumpOutput(appName, args, workingDir, new()
    {
        ["PATH"] = $"{sdkPath}:{Environment.GetEnvironmentVariable("PATH")}",
        ["DOTNET_ROOT"] = sdkPath,
        ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
    });
}

async Task<int> RunProcessAndDumpOutput(
    string name,
    string args,
    string? workingDirectory,
    Dictionary<string, string> environment)
{
    var info = new ProcessStartInfo(name, args)
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        WorkingDirectory = workingDirectory,
        CreateNoWindow = true
    };

    foreach (var (key, value) in environment)
    {
        info.Environment[key] = value;
    }

    var process = Process.Start(info);
    if(process == null)
    {
        return -1;
    }

    await process.WaitForExitAsync();

    return process.ExitCode;
}