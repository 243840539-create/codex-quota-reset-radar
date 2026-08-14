# Codex 全员额度重置雷达

一个面向 Windows 的开源预测工具，用来估算 **Codex 全员额度可能被统一重置的日期概率**。

它会把两类信息明确分开：

- **个人周期重置时间**：Codex 返回的账户级窗口时间，不属于预测。
- **全员额度重置预测**：基于公开线索、不同消息源的相互印证，以及本机观察到的异常额度回升进行概率估计。

> 预测结果不是 OpenAI 官方承诺，也不保证额度一定会在某天重置。

## 功能

- 按日期显示全员重置概率、候选日期范围和总体置信度。
- 支持手工录入 X（Twitter）等公开渠道的预告线索。
- 提供“一键搜索并添加”，可分类搜索内部人士/官方 X、OpenAI 官方与状态信息、GitHub、Reddit、新闻和博客；X 与 Reddit 默认通过免登录网页检索打开。
- 可从剪贴板自动识别链接、X 作者、中文或英文日期、信息类别与建议可信度。
- 内置已知高信号来源的初始可靠度，包括 `@thsottiaux`、`@sama`、`@OpenAIDevs` 和 `@OpenAI`。
- 单一消息源会被限制在较低置信度；多个独立来源相互印证时提高权重。
- 线索命中或落空后，自动校准该来源后续的预测权重。
- 将个人正常周期重置与疑似全员统一重置分开，避免混淆。
- 预测数据只保存在本机，不需要 X API，也不会上传 Codex 用量或线索数据。

## 下载与运行

在仓库的 **Releases** 页面下载 `Codex.zip`，解压后运行：

```text
Codex全员重置雷达.exe
```

系统要求：Windows 10/11 x64。发布包为独立运行版本，不要求预先安装 .NET。

## 使用方法

1. 启动软件，查看当前候选日期、每日概率和预测依据。
2. 点击“一键搜索并添加”，选择要搜索的信息类别；程序会打开对应搜索页和添加窗口。
3. 复制搜索结果的链接或整段文字，点击“从剪贴板识别”，检查自动填写的作者、日期、类别和可信度。
4. 随着独立来源增加或实际结果出现，软件会重新计算概率。

建议保留原帖链接并区分“明确预告”与普通猜测。账号身份、历史准确率、发布时间距离目标日期的远近，都会影响线索质量。

## 预测原则

该项目使用启发式概率模型，而不是官方后台数据：

- 单条 X 帖子不能形成高置信结论。
- 独立作者对相近日期的预测会产生交叉验证加权。
- 同一作者重复发帖不会被当作多个独立来源。
- 已验证的历史命中率会调整作者可靠度。
- 正常的个人五小时/每周窗口重置不会被计入“全员重置”。

概率代表当前证据下的相对可能性，不是严格统计学保证。

## 从源码构建

需要 .NET SDK 10.0.301 或兼容的 .NET 10 SDK：

```powershell
dotnet build .\CodexQuotaResetRadar.slnx -c Release
dotnet test .\tests\WindexBar.Core.Tests\WindexBar.Core.Tests.csproj -c Release -p:NuGetAudit=false
dotnet publish .\src\QuotaResetRadar.Windows\QuotaResetRadar.Windows.csproj -c Release -r win-x64 --self-contained true
```

## 数据位置与隐私

预测线索与校准历史默认保存在：

```text
%APPDATA%\WindexBar\quota-forecast.json
```

程序不会自动抓取或上传 X 内容。用户录入公开线索时，应遵守相应平台规则，并自行判断消息来源是否可信。

## 开源说明

本项目的 Codex 用量读取与部分基础代码基于 MIT 许可的 [WindexBar](https://github.com/myagmb28Dev/WindexBar)；原作者版权和许可文本保留在 [LICENSE](LICENSE) 中，详见 [NOTICE](NOTICE.md)。

## 许可证

MIT License。
