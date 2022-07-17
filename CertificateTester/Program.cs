// Small utility app to validate migration scenarios between .NET 6.0 and .NET 7.0
using Microsoft.AspNetCore.Certificates.Generation;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

internal class Program
{
    // To run these tests yourself, adjust the paths accordingly. Certificate tester starts in /bin/Debug/net7.0, so you either
    // need to provide full paths here or walk up the directory hierarchy structure.
    static readonly string dotnet6Path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "../../../../../net60sdk"));
    static readonly string dotnet6SdkPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "../../../../../net60sdk"));
    static readonly string dotnet7Path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "../../../../../../aspnetcore/.dotnet/dotnet"));

    // The .NET SDK points to the SDK in the ASP.NET Core repository. You need to build the repository with eng\build.sh -pack
    // and then navigate to .dotnet/shared/Microsoft.AspNetCore.App, rename the 7.0.0-dev folder to the 7.0.0-preview-... folder
    // so that when you run the .NET 7.0 application you use the updated bits. (otherwise you'll use the older bits and will not
    // see the changes)
    static readonly string dotnet7SdkPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "../../../../../../aspnetcore/.dotnet"));

    // Make sure that you have manually created and built this apps (dotnet new webapp -o ...) with the appropriate SDKs
    // as the script uses --no-build to speed things up.
    static readonly string dotnet7WebAppPath = Path.GetFullPath("../../../../../webapp7");
    static readonly string dotnet6WebAppPath = Path.GetFullPath("../../../../../webapp6");

    private static async Task Main(string[] args)
    {
        // Login keychain
        DisplayCertificates(StoreName.My, StoreLocation.CurrentUser);

        // Login keychain but only trusted (we don't use this, just displaying for completeness).
        DisplayCertificates(StoreName.Root, StoreLocation.CurrentUser);

        // System keychain
        DisplayCertificates(StoreName.Root, StoreLocation.LocalMachine);

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

        Console.WriteLine("Check dotnet 6 SDK");
        await RunDotnetApp(
            dotnet6Path,
            "--info",
            dotnet6SdkPath);

        Console.WriteLine("Check dotnet 7 SDK");
        await RunDotnetApp(
            dotnet7Path,
            "--info",
            dotnet7SdkPath);

        // Scenario 1: We have cleaned all the certificates on the machine, we are creating a .NET 6.0 certificate
        // and running a .NET 6.0 and a .NET 7.0 app.
        // .NET 6.0 will prompt for the key (as it does today).
        // .NET 7.0 will display a message indicating that no certificate is available.

        await Run60And70AppsUsingCertificate(client);

        // Cleanup for next scenario
        Console.WriteLine("Removing all the certificates.");
        manager.RemoveAllCertificates(StoreName.My, StoreLocation.CurrentUser);
        manager.RemoveAllCertificates(StoreName.Root, StoreLocation.CurrentUser);

        // Scenario 2: Same but .NET 6.0 trusted the certificate
        await Run60And70AppsUsingCertificate(client, trust: true);

        // Cleanup for next scenario
        Console.WriteLine("Removing all the certificates.");
        manager.RemoveAllCertificates(StoreName.My, StoreLocation.CurrentUser);
        manager.RemoveAllCertificates(StoreName.Root, StoreLocation.CurrentUser);

        // Scenario 3: We have cleaned all the certificates on the machine, we are creating a .NET 7.0 certificate
        // and running a .NET 6.0 and a .NET 7.0 app.
        // .NET 6.0 will prompt for the key (as it does today).
        // .NET 7.0 will not prompt and run successfully

        await Run60And70AppsUsingCertificate(client, use70SdkDevCerts: true);

        // Cleanup for next scenario
        Console.WriteLine("Removing all the certificates.");
        manager.RemoveAllCertificates(StoreName.My, StoreLocation.CurrentUser);
        manager.RemoveAllCertificates(StoreName.Root, StoreLocation.CurrentUser);

        // Scenario 2: Same but .NET 7.0 trusted the certificate
        await Run60And70AppsUsingCertificate(client, trust: true);

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
                ["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = sdkPath,
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
            if (process == null)
            {
                return -1;
            }

            await process.WaitForExitAsync();
            Console.WriteLine(await process.StandardOutput.ReadToEndAsync());
            Console.WriteLine(await process.StandardError.ReadToEndAsync());

            return process.ExitCode;
        }

        async Task Run60And70AppsUsingCertificate(
            HttpClient client,
            bool trust = false,
            bool use70SdkDevCerts = false)
        {
            Console.WriteLine("Creating a new certificate with dotnet dev-certs https using the 6.0 SDK.");
            await RunDotnetApp(
                !use70SdkDevCerts ? dotnet6Path : dotnet7Path,
                $"dev-certs https{(trust ? " --trust" : "")}",
                !use70SdkDevCerts ? dotnet6SdkPath : dotnet7SdkPath);

            Console.WriteLine("Running a .NET 7.0 web application.");
            var net7App = RunDotnetApp(
                dotnet7Path,
                "run --no-build",
                dotnet7SdkPath,
                dotnet7WebAppPath);

            Console.WriteLine("Requesting https://localhost:5001/");
            await Task.Delay(10_000);
            try
            {
                Console.WriteLine(await client.GetStringAsync("https://localhost:5001"));
                throw new InvalidOperationException("The app should not start because we are binding to HTTPS without the cert being valid.");
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to connect to the application");
            }

            Console.WriteLine("Running a .NET 6.0 web application.");
            var net6App = RunDotnetApp(
                dotnet6Path,
                "run --no-build",
                dotnet6SdkPath,
                dotnet6WebAppPath);

            Console.WriteLine("Requesting https://localhost:5001/");
            await Task.Delay(10_000);
            try
            {
                Console.WriteLine(await client.GetStringAsync("https://localhost:5001"));
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to connect to the application");
            }
        }
    }
}