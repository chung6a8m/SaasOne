# M0 package selection and restore findings

2026-11-06. The first real restore on SDK 10.0.403 / PowerShell 7.6.9 failed NuGet audit, not an ignored warning. Run 42206885210 reported vulnerable transitive AngleSharp 1.4.0, MySqlConnector 2.4.0, ImageSharp 3.1.11, Cryptography.Pkcs 9.0.11/10.0.7/10.0.8 and Cryptography.Xml 10.0.8. Build/tests had not run.

Keep audit and warnings-as-errors enabled. Centrally pin AngleSharp 1.8.5, MySqlConnector 2.6.2 and System.Security.Cryptography.Pkcs/Xml 10.0.14, then regenerate genuine NuGet locks and verify. These pins do not select MySQL as an application provider; SQL Server remains the only application provider.

Replace the all-modules `OrchardCore.Application.Cms.Core.Targets` meta-package with explicit `OrchardCore.Application.Targets`, CMS `OrchardCore.Infrastructure`, `OrchardCore.OpenId` and the theme target, all 3.0.1. This retains the chosen Orchard stack and resolves YesSql/OpenIddict without importing every CMS media/cloud module. M0 has no tenant middleware; M1 owns reviewed CMS registrations and the minimal feature/recipe allowlist. Do not describe the M0 liveness endpoint as a functioning CMS tenant.

ImageSharp/media is outside M0 and customer uploads are closed. Do not downgrade or suppress its advisories, bypass license enforcement, or infer a commercial license. Introducing media later requires security, license and compatibility review before adding that dependency. The explicit package composition is an implementation selection for owner review in the M0 PR.

Sources: official package metadata at https://www.nuget.org/packages/OrchardCore.Application.Cms.Core.Targets/3.0.1, https://www.nuget.org/packages/OrchardCore.Infrastructure/3.0.1, https://www.nuget.org/packages/OrchardCore.OpenId/3.0.1, https://www.nuget.org/packages/AngleSharp/1.8.5, https://www.nuget.org/packages/MySqlConnector/2.6.2, https://www.nuget.org/packages/System.Security.Cryptography.Pkcs/10.0.14 and https://docs.sixlabors.com/articles/imagesharp/.
