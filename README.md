# Fast Share

Fast, direct file sharing between Windows PCs. One app does both jobs: share folders from your PC, or connect to
someone else's with a short code - then browse their shares in an Explorer-style window and download, send,
rename and delete. Only one side needs a forwarded port; the other connects out and can still share back.
Built for raw speed: big files over 8 parallel connections, thousands of small files packed into streams, TLS 1.3
throughout, and every PC that connects has to be approved first.

**Download:** https://teamwolfgamers.online/fast-share (installer or portable .rar)

The app checks that page for updates when it starts and every hour (pattywatty.duckdns.org is the backup address)
and installs a newer version itself. How to use it is in [GUIDE.md](GUIDE.md), which also ships with the app.

## Building

Windows, with the [.NET 10 SDK](https://dotnet.microsoft.com/download). For the downloads also
[WinRAR](https://www.win-rar.com) (the portable .rar) and [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(the installer, from `installer.iss`).

```
dotnet run --project Tests -c Release          the checks (local only, temp folders)
dotnet publish FastShare.csproj -c Release -o out -p:Version=1.2.3
```

The published folder is `FastShare.exe` (self-contained, single file) plus five native WPF DLLs that .NET drops
beside it (`D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll`,
`wpfgfx_cor3.dll` - Microsoft's, from the SDK; they are not in this repository). `UpdateServer\` is an optional
console tool for serving builds from the maintainer's PC; `Tests\` holds the checks.

This folder is the app's source. In Patrick's project it sits inside the project folder, whose `build.bat` runs
`build-app.bat <version> <project folder>` (checks, publish, the version stamped in) and leaves the app in
`Build\`; `package-app.bat` makes the downloads. Without that folder around it the version is 0.0.0 unless you
pass `-p:Version=`.

## More

Made by Patrick - more apps and tools at https://teamwolfgamers.online
