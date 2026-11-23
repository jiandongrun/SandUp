# 随包第三方许可

发布脚本读取实际发布的依赖，提取 NuGet 包中的许可和声明，并把下面的上游原件、字体许可和 SandUp 的 MIT 许可证写入 `SandUp-Licenses.txt`。分发 `SandUp.exe` 时请附上同一次发布生成的声明文件。

| 组件 | 许可证及声明出处 |
| --- | --- |
| .NET 运行时 10.0.15 | MIT 及第三方声明，随 .NET 运行时 NuGet 包提供。 |
| Windows Desktop 运行时 10.0.15 | MIT 随 NuGet 包提供。补充 [.NET 同版 WPF 声明](https://github.com/dotnet/dotnet/blob/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/wpf/THIRD-PARTY-NOTICES.TXT)和 [Windows Forms 声明](https://github.com/dotnet/dotnet/blob/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/winforms/THIRD-PARTY-NOTICES.TXT)。 |
| Microsoft.Data.Sqlite.Core 10.0.15 | 补充 [.NET 同版 EF Core MIT 原文](https://github.com/dotnet/dotnet/blob/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/efcore/LICENSE.txt)。 |
| SQLite 3.54.1 | NuGet 包内声明及 [SQLite 公有领域说明](https://www.sqlite.org/copyright.html)。 |
| SQLitePCLRaw 3.0.5 | 上游 [Apache-2.0 原文](https://github.com/ericsink/SQLitePCL.raw/blob/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT)与 [NOTICE](https://github.com/ericsink/SQLitePCL.raw/blob/ed046114d5a30534e13294d94d78eb73de896ad4/NOTICE.TXT)。 |
| Noto Sans SC 2.004 | SIL OFL 1.1 及版权声明随[源代码字体文件](../../src/SandUp.Desktop/Assets/Fonts)保存。 |

更新运行时或 NuGet 依赖时，请按包内元数据核对新版本的原版许可和 NOTICE，并更新相应的补充文件。
