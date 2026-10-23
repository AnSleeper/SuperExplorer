# AGENTS.md — 给在本仓库工作的 AI 助手

## 项目一句话

超级资源管理器：.NET 8 WPF 的 Windows 多面板媒体资源浏览器，最高优先级是**内存占用尽可能小**（用户长时间常驻后台、同机跑 AI 绘图等重负载）。详细定位、理念、未实现功能见 README。

## 硬约束（违反任何一条都是事故）

- 一切产物只放项目目录内，**严禁占用 C 盘**；编译前把缓存重定向进项目：
  `NUGET_PACKAGES=<项目>/.nuget`、`DOTNET_CLI_HOME=XDG_DATA_HOME=<项目>/.dotnet`
- `dist\` 是用户本机的部署目录，里面有用户个人数据（settings.json、日志），**不删除、不入库**（.gitignore 已排除）
- 删除范围仅限 `bin/`、`obj/`、`_publish_tmp/`
- **验证程序时禁止模拟鼠标/键盘**（会抢占用户前台）。只允许后台方式：向指定测试窗口发消息（SendMessage）、读日志、读文件
- 本机到 GitHub 网络不稳：失败先重试几轮（间隔 15s），直连不通回退本地代理 `127.0.0.1:7897`，反之亦然

## 版本迭代流程

1. 改代码 → 同步更新 `README.md` 的「更新记录」章节
2. bump `src/SuperExplorer/SuperExplorer.csproj` 里的 `<Version>`
3. 提交并推送（push 失败按上面的重试策略）
4. 运行 `powershell -ExecutionPolicy Bypass -File tools\release.ps1`
   它会自动：dotnet publish → 打 zip（`_publish_tmp\`）→ 创建/更新对应 tag 的 GitHub Release → 上传附件（已存在的 Release 会更新说明并替换同名附件）。GitHub 凭据自动取自 Windows 凭据管理器，无需配置。

## 代码风格

- 事件驱动 code-behind，无 MVVM 框架、无第三方依赖；`Nullable`/`ImplicitUsings` 均关闭（新文件需要显式 using）
- 中文注释解释"为什么"，代码保持防御式 try-catch（UI 不允许崩）
- 性能相关改动先想内存：卡片虚拟化、缩略图按需加载、并发限制这些机制不许倒退
