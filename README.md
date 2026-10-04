# novolis-reach

Headless Reach protocol, transport, client session, and Windows host libraries
for the Novolis platform. Product executables stay in `novolis-apps`.

## Packages

| Package | Purpose |
|---|---|
| `Novolis.Reach.Protocol` | Messages, session machine, and wire types |
| `Novolis.Reach.Transport` | Datagram codec over Novolis transports |
| `Novolis.Reach.Client` | Headless client session (no Avalonia) |
| `Novolis.Reach.Host.Server` | Headless host service that accepts clients |
| `Novolis.Reach.Host.Windows.Session` | Interactive-session capture helper (`net10.0-windows`) |

Avalonia chrome lives in `Novolis.Avalonia.Reach`.

## Run the product hosts

```powershell
dotnet run --project d:\novolis\novolis-apps\src\Reach\Reach.Host.Windows.Service\Reach.Host.Windows.Service.csproj -p:NovolisUseProjectReferences=true
dotnet run --project d:\novolis\novolis-apps\src\Reach\Reach.Host.Windows\Reach.Host.Windows.csproj -p:NovolisUseProjectReferences=true
dotnet run --project d:\novolis\novolis-apps\src\Reach\Reach.Client.Windows\Reach.Client.Windows.csproj -p:NovolisUseProjectReferences=true
```
