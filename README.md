# SandUp · 沙立

SandUp 是一款会挥手提醒你休息的 Windows 番茄钟。工作快结束时，桌面边缘的沙漏会站起来挥手。全屏休息会提示做什么动作，也可以随时应急退出。

- 自由编排工作、休息和可重复的组合，预览完整计划。
- 在休息前写下回来要接着做的事，休息结束后选择什么时候开始下一轮。
- 查看今日工作与休息时间、历史趋势和已记录的活动。
- 计时记录、备忘录和设置保存在本机，无需注册账号。

## 下载与使用

当前版本为 **v1.17.24**，适用于 Windows x64。发布后，可在 GitHub 仓库的 Releases 中下载 Windows 试用版。将 `SandUp.exe` 与同版 `SandUp-Licenses.txt` 放在同一目录，双击程序即可运行。试用版未数字签名，已验证的项目和实机验收范围见 [测试版状态](docs/release-status.md)。

工作和休息计划可在设置页修改。开机自启动默认关闭。用户数据位于 `%LOCALAPPDATA%\HealthyWorkClock`。更多操作见 [使用说明](docs/user-guide.md)。

## 从源码运行

需要 Windows x64、PowerShell 和 `global.json` 指定的 .NET SDK（目前为 10.0.404）。在项目根目录运行以下命令。

```powershell
./tools/dev.ps1 Restore
./tools/dev.ps1 Build
./tools/dev.ps1 Test
./tools/dev.ps1 Run
```

运行 `./tools/dev.ps1 Publish` 可将自带 .NET 运行时的程序、说明文档和许可声明生成到 `output/release/current`。更新前先从托盘退出该目录中的程序。

## 贡献与反馈

报告问题时，请附上 SandUp 版本、Windows 版本和可复现步骤。截图或日志中的个人记录、备忘录和用户名请先遮盖。提交代码前请运行上述构建和测试。计时状态、存储及验收规则见 [开发文档](docs/development/00-开发交接总览.md)。

应用里的「耐久度」根据计时与自报活动生成，用来提醒休息。[研究依据与默认参数说明](docs/research/durability-evidence-20261119.md)

## 许可证

SandUp 源码按 [MIT 许可证](LICENSE) 发布。Noto Sans SC 及随包第三方组件沿用各自许可。[第三方组件与声明](tools/third-party-notices/README.md)
