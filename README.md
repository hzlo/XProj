# XProj

XProj 是一个轻量的 Windows 本地项目与命令管理器，用于按分组管理项目目录，并集中启动、停止和查看常用命令。项目使用 C# 与 WPF 开发，所有配置均保存在本机。

## 功能

- 使用任意层级的分组整理项目
- 添加、编辑、删除和搜索本地项目
- 为每个项目配置多条启动命令
- 启动、停止和重启命令，实时查看标准输出、错误输出及退出码
- 从应用内快速打开项目目录、终端或 Visual Studio Code
- 深色与浅色模式运行时切换，可分别配置主前景色和背景色，并同步切换主题 Logo
- 使用 Windows 系统字体分别配置界面与实时日志，并限制实时日志可见行数
- 配置关闭窗口时最小化到托盘或完全退出
- 重复启动时唤醒已有窗口，始终只保留一个应用实例
- 启动时自动检查 GitHub Release 更新（成功结果缓存 24 小时），并支持从设置或托盘手动检查
- 导入或导出包含分组、项目、命令和应用设置的 JSON 配置
- 退出时安全回收所有由 XProj 启动的进程树
- 创建运行方案，按场景批量启动命令，并可在切换方案前停止方案外命令
- 启动命令时自动读取最新系统与用户环境变量，并支持项目级变量覆盖
- 查看运行命令的运行时长
- 搜索和筛选实时日志
- 自动按日备份本地配置，保留最近 7 份备份

## 下载

请从仓库的 **Releases** 页面下载最新版本。每个版本提供两种 Windows x64 压缩包：

| 发布包 | 适用场景 | 运行要求 |
| --- | --- | --- |
| `XProj-*-win-x64-self-contained.zip` | 推荐普通用户使用 | 无需预装 .NET，文件体积较大 |
| `XProj-*-win-x64-framework-dependent.zip` | 已安装运行环境的用户 | 需要 [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) |

下载后解压 ZIP，运行 `XProj.exe` 即可。Windows 首次运行从网络下载的程序时可能显示安全提示，请确认文件来自本仓库的 Release。

## 使用说明

1. 创建分组，用于整理不同类型的项目。
2. 添加项目并选择本地工作目录。
3. 为项目添加命令，例如 `dotnet run`、`npm run dev` 或 `java -jar app.jar`。
4. 点击命令即可启动进程，在右侧实时日志区域查看输出。
5. 使用标题栏运行方案按钮按场景批量启动命令。
6. 使用日志区域左侧的搜索框筛选输出。
7. 使用标题栏设置按钮调整主题、字体、关闭行为或导入导出配置。主题颜色使用 `#RRGGBB` 格式，其他界面颜色会自动派生；前景与背景对比度过低时不会保存。

XProj 通过 Windows `cmd.exe` 执行命令。关闭到系统托盘时，已启动的命令会继续运行；选择完全退出时，XProj 会先停止所有托管进程。

## 配置数据

应用数据默认保存在：

```text
%LOCALAPPDATA%\ProjectManagerWpf\data.json
```

删除 XProj 中的项目或分组不会删除磁盘上的项目文件。删除分组时，其直属子分组与项目会移动到上一级。自动配置备份保存在同一数据目录下，文件名类似 `data.backup-20260731.json`。

可以在设置中导出完整 JSON 配置进行备份或迁移。为避免运行状态与配置不一致，存在正在运行的命令时不能导入配置。

## 本地构建

要求：

- Windows 10 或更高版本
- .NET 10 SDK

```powershell
git clone https://github.com/hzlo/XProj.git
cd XProj
dotnet build .\XProj.sln
dotnet run --project .\XProj.Desktop\XProj.Desktop.csproj
```

运行冒烟测试：

```powershell
dotnet run --project .\ProjectManager.Wpf.SmokeTests\ProjectManager.Wpf.SmokeTests.csproj
```

## 发布

仓库使用 GitHub Actions 管理 Release。推送 `v*` 格式的标签后，工作流会：

1. 运行 Release 构建和冒烟测试。
2. 生成 Windows x64 自包含版本。
3. 生成 Windows x64 框架依赖版本。
4. 将两个 ZIP 上传到对应的 GitHub Release。

示例：

```powershell
git tag v1.1.0
git push origin v1.1.0
```

## 技术栈

- C# / WPF
- .NET 10 (`net10.0-windows`)
- JSON 本地持久化
- GitHub Actions

## 插件开发

仓库目前包含一个全局 Markdown 备忘录插件示例：

```text
XProj.Plugin.Abstractions/
XProj.Plugin.Notes/
```

插件通过 `XProj.Plugin.Abstractions` 中的 `IXProjPlugin` 和 `PluginHostContext` 接入宿主。当前版本采用解决方案内项目引用，插件在应用启动时由宿主创建；这为后续改造成独立 DLL 扫描加载保留了边界，但目前还不是运行时动态加载机制。

备忘录插件的数据保存在：

```text
%LOCALAPPDATA%\ProjectManagerWpf\notes\
```

每个 Markdown 文件对应一篇全局笔记，不绑定 XProj 项目。插件页面提供笔记列表、搜索、编辑、自动保存和 Markdown 预览。

## 许可证

本项目采用 [MIT License](LICENSE)，允许自由使用、修改和分发，但需保留版权与许可证声明。
