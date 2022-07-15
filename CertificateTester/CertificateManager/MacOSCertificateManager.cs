// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Microsoft.AspNetCore.Certificates.Generation;

internal sealed class MacOSCertificateManager : CertificateManager
{
    private const string CertificateSubjectRegex = "CN=(.*[^,]+).*";
    private static readonly string MacOSUserKeyChain = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/Library/Keychains/login.keychain-db";
    // private const string MacOSSystemKeyChain = "/Library/Keychains/System.keychain";
    // private const string MacOSFindCertificateCommandLine = "security";
    // private const string MacOSFindCertificateCommandLineArgumentsFormat = "find-certificate -c {0} -a -Z -p " + MacOSSystemKeyChain;
    // private const string MacOSFindCertificateOutputRegex = "SHA-1 hash: ([0-9A-Z]+)";
    private const string MacOSVerifyCertificateCommandLine = "security";
    private const string MacOSVerifyCertificateCommandLineArgumentsFormat = $"verify-cert -c {{0}} -s {{1}}";
    private const string MacOSRemoveCertificateTrustCommandLine = "security";
    private const string MacOSRemoveCertificateTrustCommandLineArgumentsFormat = "remove-trusted-cert {0}";
    private const string MacOSDeleteCertificateCommandLine = "sudo";
    private const string MacOSDeleteCertificateCommandLineArgumentsFormat = "security delete-certificate -Z {0} {1}";
    private const string MacOSTrustCertificateCommandLine = "security";
    private static readonly string MacOSTrustCertificateCommandLineArguments = $"add-trusted-cert -r trustRoot -p basic -p ssl -k {MacOSUserKeyChain} ";
    private static readonly string MacOSUserHttpsCertificateLocation = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".aspnet", "https");

    private const string MacOSAddCertificateToKeyChainCommandLine = "security";
    private static readonly string MacOSAddCertificateToKeyChainCommandLineArgumentsFormat = "import {0} -k " + MacOSUserKeyChain + " -t cert -f pkcs12 -P {1} -A";

    public const string InvalidCertificateState = "The ASP.NET Core developer certificate is in an invalid state. " +
        "To fix this issue, run the following commands 'dotnet dev-certs https --clean' and 'dotnet dev-certs https' to remove all existing ASP.NET Core development certificates " +
        "and create a new untrusted developer certificate. " +
        "On macOS or Windows, use 'dotnet dev-certs https --trust' to trust the new certificate.";

    public const string KeyNotAccessibleWithoutUserInteraction =
        "The application is trying to access the ASP.NET Core developer certificate key. " +
        "A prompt might appear to ask for permission to access the key. " +
        "When that happens, select 'Always Allow' to grant 'dotnet' access to the certificate key in the future.";

    private static readonly TimeSpan MaxRegexTimeout = TimeSpan.FromMinutes(1);

    public MacOSCertificateManager()
    {
    }

    internal MacOSCertificateManager(string subject, int version)
        : base(subject, version)
    {
    }

    protected internal override void TrustCertificateCore(X509Certificate2 publicCertificate)
    {
        if (IsTrusted(publicCertificate))
        {
            Log.MacOSCertificateAlreadyTrusted();
            return;
        }

        var tmpFile = Path.GetTempFileName();
        try
        {
            ExportCertificate(publicCertificate, tmpFile, includePrivateKey: false, password: null, CertificateKeyExportFormat.Pfx);
            if (Log.IsEnabled())
            {
                Log.MacOSTrustCommandStart($"{MacOSTrustCertificateCommandLine} {MacOSTrustCertificateCommandLineArguments}{tmpFile}");
            }
            using (var process = Process.Start(MacOSTrustCertificateCommandLine, MacOSTrustCertificateCommandLineArguments + tmpFile))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    Log.MacOSTrustCommandError(process.ExitCode);
                    throw new InvalidOperationException("There was an error trusting the certificate.");
                }
            }
            Log.MacOSTrustCommandEnd();
        }
        finally
        {
            try
            {
                if (File.Exists(tmpFile))
                {
                    File.Delete(tmpFile);
                }
            }
            catch
            {
                // We don't care if we can't delete the temp file.
            }
        }
    }

    public override CheckCertificateStateResult CheckCertificateState(X509Certificate2 candidate, bool interactive)
    {
        var certificatePath = Path.Combine(MacOSUserHttpsCertificateLocation, GetCertificateFileName(candidate));
        if (File.Exists(certificatePath))
        {
            return new CheckCertificateStateResult(true, null);
        }
        else
        {
            return new CheckCertificateStateResult(false, KeyNotAccessibleWithoutUserInteraction);
        }
    }

    internal override void CorrectCertificateState(X509Certificate2 candidate)
    {
        try
        {
            EnsureCertificateFolder();
            var certificatePath = Path.Combine(MacOSUserHttpsCertificateLocation, GetCertificateFileName(candidate));
            ExportCertificate(candidate, certificatePath, includePrivateKey: true, null, CertificateKeyExportFormat.Pfx);
        }
        catch (Exception ex)
        {
            Log.MacOSAddCertificateToKeyChainError($@"There was an error saving the certificate into the user profile folder '{candidate.Thumbprint}'.

{ex.Message}");
        }
    }

    public override bool IsTrusted(X509Certificate2 certificate)
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            ExportCertificate(certificate, tmpFile, includePrivateKey: false, password: null, CertificateKeyExportFormat.Pem);
            var subjectMatch = Regex.Match(certificate.Subject, CertificateSubjectRegex, RegexOptions.Singleline, MaxRegexTimeout);
            if (!subjectMatch.Success)
            {
                throw new InvalidOperationException($"Can't determine the subject for the certificate with subject '{certificate.Subject}'.");
            }
            var subject = subjectMatch.Groups[1].Value;
            using var checkTrustProcess = Process.Start(new ProcessStartInfo(
                MacOSVerifyCertificateCommandLine,
                string.Format(CultureInfo.InvariantCulture, MacOSVerifyCertificateCommandLineArgumentsFormat, tmpFile, subject))
            {
                RedirectStandardOutput = true,
                // Do this to avoid showing output to the console when the cert is not trusted. It is trivial to export the cert
                // and replicate the command to see details.
                RedirectStandardError = true,
            });
            checkTrustProcess!.WaitForExit();
            return checkTrustProcess.ExitCode == 0;
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    protected internal override void RemoveCertificateFromTrustedRoots(X509Certificate2 certificate)
    {
        if (IsTrusted(certificate)) // On OSX this check just ensures its on the system keychain
        {
            // A trusted certificate in OSX is installed into the system keychain and
            // as a "trust rule" applied to it.
            // To remove the certificate we first need to remove the "trust rule" and then
            // remove the certificate from the keychain.
            // We don't care if we fail to remove the trust rule if
            // for some reason the certificate became untrusted.
            // Trying to remove the certificate from the keychain will fail if the certificate is
            // trusted.
            try
            {
                RemoveCertificateTrustRule(certificate);
            }
            catch
            {
            }

            // Making the certificate trusted will automatically added it to the user key chain
            RemoveCertificateFromKeyChain(MacOSUserKeyChain, certificate);

            var certificatePath = Path.Combine(MacOSUserHttpsCertificateLocation, GetCertificateFileName(certificate));
            if (File.Exists(certificatePath))
            {
                File.Delete(certificatePath);
            }
        }
        else
        {
            Log.MacOSCertificateUntrusted(GetDescription(certificate));
        }
    }

    internal static void RemoveCertificateFromKeyChain(string keyChain, X509Certificate2 certificate)
    {
        var processInfo = new ProcessStartInfo(
            MacOSDeleteCertificateCommandLine,
            string.Format(
                CultureInfo.InvariantCulture,
                MacOSDeleteCertificateCommandLineArgumentsFormat,
                certificate.Thumbprint.ToUpperInvariant(),
                keyChain
            ))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Log.IsEnabled())
        {
            Log.MacOSRemoveCertificateFromKeyChainStart(keyChain, GetDescription(certificate));
        }

        using (var process = Process.Start(processInfo))
        {
            var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Log.MacOSRemoveCertificateFromKeyChainError(process.ExitCode);
                throw new InvalidOperationException($@"There was an error removing the certificate with thumbprint '{certificate.Thumbprint}'.

{output}");
            }
        }

        Log.MacOSRemoveCertificateFromKeyChainEnd();
    }

    internal static void RemoveCertificateTrustRule(X509Certificate2 certificate)
    {
        Log.MacOSRemoveCertificateTrustRuleStart(GetDescription(certificate));
        var certificatePath = Path.GetTempFileName();
        try
        {
            var certBytes = certificate.Export(X509ContentType.Cert);
            File.WriteAllBytes(certificatePath, certBytes);
            var processInfo = new ProcessStartInfo(
                MacOSRemoveCertificateTrustCommandLine,
                string.Format(
                    CultureInfo.InvariantCulture,
                    MacOSRemoveCertificateTrustCommandLineArgumentsFormat,
                    certificatePath
                ));
            using var process = Process.Start(processInfo);
            process!.WaitForExit();
            if (process.ExitCode != 0)
            {
                Log.MacOSRemoveCertificateTrustRuleError(process.ExitCode);
            }
            Log.MacOSRemoveCertificateTrustRuleEnd();
        }
        finally
        {
            try
            {
                if (File.Exists(certificatePath))
                {
                    File.Delete(certificatePath);
                }
            }
            catch
            {
                // We don't care about failing to do clean-up on a temp file.
            }
        }
    }

    // We don't have a good way of checking on the underlying implementation if ti is exportable, so just return true.
    protected internal override bool IsExportable(X509Certificate2 c) => true;

    protected internal override X509Certificate2 SaveCertificateCore(X509Certificate2 certificate, StoreName storeName, StoreLocation storeLocation)
    {
        if (Log.IsEnabled())
        {
            Log.MacOSAddCertificateToKeyChainStart(MacOSUserKeyChain, GetDescription(certificate));
        }

        try
        {
            // We do this for backwards compatibility with previous versions. .NET 7.0 and onwards will ignore the
            // certificate on the keychain and load it directly from disk.
            certificate = SaveCertificateToUserKeychain(certificate);
        }
        catch (Exception ex)
        {

            Log.MacOSAddCertificateToKeyChainError($@"There was an error saving the certificate into the user keychain '{certificate.Thumbprint}'.

{ex.Message}");
        }

        try
        {
            var certBytes = certificate.Export(X509ContentType.Pfx);
            EnsureCertificateFolder();
            var certificatePath = Path.Combine(MacOSUserHttpsCertificateLocation, GetCertificateFileName(certificate));
            File.WriteAllBytes(certificatePath, certBytes);
        }
        catch (Exception ex)
        {
            Log.MacOSAddCertificateToKeyChainError($@"There was an error saving the certificate into the user profile folder '{certificate.Thumbprint}'.

{ex.Message}");
        }

        Log.MacOSAddCertificateToKeyChainEnd();

        return certificate;
    }

    internal static X509Certificate2 SaveCertificateToUserKeychain(X509Certificate2 certificate)
    {
        // security import https.pfx -k $loginKeyChain -t cert -f pkcs12 -P password -A;
        var passwordBytes = new byte[48];
        RandomNumberGenerator.Fill(passwordBytes.AsSpan()[0..35]);
        var password = Convert.ToBase64String(passwordBytes, 0, 36);
        var certBytes = certificate.Export(X509ContentType.Pfx, password);
        var certificatePath = Path.GetTempFileName();
        File.WriteAllBytes(certificatePath, certBytes);

        var processInfo = new ProcessStartInfo(
            MacOSAddCertificateToKeyChainCommandLine,
        string.Format(
            CultureInfo.InvariantCulture,
            MacOSAddCertificateToKeyChainCommandLineArgumentsFormat,
            certificatePath,
            password
        ))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Log.IsEnabled())
        {
            Log.MacOSAddCertificateToKeyChainStart(MacOSUserKeyChain, GetDescription(certificate));
        }

        using (var process = Process.Start(processInfo))
        {
            var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Log.MacOSAddCertificateToKeyChainError($"There was an error importing the certificate into the user key chain. The process exited with code '{process.ExitCode}'");
                throw new InvalidOperationException($@"There was an error importing the certificate into the user key chain '{certificate.Thumbprint}'.
{output}");
            }
        }

        Log.MacOSAddCertificateToKeyChainEnd();

        return certificate;
    }

    internal static string GetCertificateFileName(X509Certificate2 certificate)
    {
        return $"aspnetcore-localhost-{certificate.Thumbprint}.pfx";
    }

    internal static void EnsureCertificateFolder()
    {
        if (!Directory.Exists(MacOSUserHttpsCertificateLocation))
        {
            Directory.CreateDirectory(MacOSUserHttpsCertificateLocation);
        }
    }

    protected internal override IList<X509Certificate2> GetCertificatesToRemove(StoreName storeName, StoreLocation storeLocation)
    {
        return ListCertificates(StoreName.My, StoreLocation.CurrentUser, isValid: false);
    }

    protected internal override void PopulateCertificatesFromStore(X509Store store, List<X509Certificate2> certificates)
    {
        if (store.Name! == StoreName.My.ToString() && store.Location == store.Location && Directory.Exists(MacOSUserHttpsCertificateLocation))
        {
            var certificateFiles = Directory.EnumerateFiles(MacOSUserHttpsCertificateLocation, "aspnetcore-localhost-*.pfx")
                    .Select(f => new X509Certificate2(f));

            var storeCertificates = new List<X509Certificate2>();
            base.PopulateCertificatesFromStore(store, storeCertificates);

            // Ignore the certificates that we only found on disk, this can be the result of a clean operation with the .NET 6.0 SDK tool.
            // Cleaning on .NET 6.0 produces the same effect on .NET 7.0 as cleaning with 3.1 produces on .NET 6.0, the system believes no certificate is present.
            // Left over here is not important because the size is very small and is not a common operation. We can clean this on .NET 7.0 clean if we think this
            // is important
            var onlyOnDisk = certificateFiles.Except(storeCertificates, ThumbprintComparer.Instance);

            // This can happen when the certificate was created with .NET 6.0, either because there was a previous .NET 6.0 SDK installation that created it, or
            // because the existing certificate expired and .NET 6.0 SDK was used to generate a new certificate.
            var onlyOnKeyChain = storeCertificates.Except(certificateFiles, ThumbprintComparer.Instance);

            // This is the normal case when .NET 7.0 was installed on a clean machine or after a certificate created with .NET 6.0 was "upgraded" to .NET 7.0.
            // .NET 7.0 always installs the certificate on the user keychain as well as on disk to make sure that .NET 6.0 can reuse the certificate.
            var onDiskAndKeyChain = certificateFiles.Intersect(storeCertificates, ThumbprintComparer.Instance);

            // The only times we can find a certificate on the keychain and a certificate on keychain + disk is when the certificate on disk and keychain has expired
            // and .NET 6.0 has been used to create a new certificate or when the .NET 6.0 certificate has expired and .NET 7.0 has been used to create a new certificate.
            // In both cases, the caller filters the invalid certificates out, so only the valid certificate is selected.
            certificates.AddRange(onlyOnKeyChain);
            certificates.AddRange(onDiskAndKeyChain);
        }
        else
        {
            base.PopulateCertificatesFromStore(store, certificates);
        }
    }

    internal class ThumbprintComparer : IEqualityComparer<X509Certificate2>
    {
        public static readonly ThumbprintComparer Instance = new();

        public bool Equals(X509Certificate2? x, X509Certificate2? y) =>
            EqualityComparer<string>.Default.Equals(x?.Thumbprint, y?.Thumbprint);

        public int GetHashCode([DisallowNull] X509Certificate2 obj) =>
            EqualityComparer<string>.Default.GetHashCode(obj.Thumbprint);
    }

    protected internal override void RemoveCertificateFromUserStoreCore(X509Certificate2 certificate)
    {
        try
        {
            var certificatePath = Path.Combine(MacOSUserHttpsCertificateLocation, GetCertificateFileName(certificate));
            if (File.Exists(certificatePath))
            {
                File.Delete(certificatePath);
            }
        }
        catch (Exception ex)
        {
            Log.MacOSAddCertificateToKeyChainError($@"There was an error importing the certificate into the user key chain '{certificate.Thumbprint}'.

{ex.Message}");
        }
    }
}
