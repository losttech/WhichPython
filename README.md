# WhichPython
Cross-platform library, that lists available Python environments

[![WhichPython on NuGet](https://img.shields.io/nuget/v/WhichPython)](https://www.nuget.org/packages/WhichPython/)

## Sample (see app folder)

```csharp
foreach (var environment in PythonEnvironment.EnumerateEnvironments()
                                    .Concat(CondaEnvironment.EnumerateCondaEnvironments())) {
    Console.WriteLine(this.HomeOnly
        ? environment.Home?.FullName
        : $"{environment.LanguageVersion?.ToString(2) ?? "??"}-{environment.Architecture?.ToString() ?? "???"} @ {environment.Home}");
}
```
