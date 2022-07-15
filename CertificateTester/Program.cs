// Small utility app to validate migration scenarios between .NET 6.0 and .NET 7.0
using Microsoft.AspNetCore.Certificates.Generation;
using System.Security.Cryptography.X509Certificates;

// We will a modified version of the certificate manager to setup
// the different initial scenarios between .NET 6.0 and .NET 7.0
// The tool will invoke dotnet dev-certs in several situations as well
// as run a .NET 7.0 app to validate the user experience (only a log message will be displayed when
// it can't access the HTTPS certificate).
var manager = CertificateManager.Instance;

// Clean the state on the machine
manager.RemoveAllCertificates(StoreName.My, StoreLocation.CurrentUser);
manager.RemoveAllCertificates(StoreName.Root, StoreLocation.CurrentUser);