# DSH Portable · Windows

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（DSH）的 **Windows 便携发行版**：
解压即用、无需预装 Node / .NET（只有用 PDF 阅读工具时才需要系统 Python 3 + `pymupdf`），
数据全部收在包内 `dsh-home\`，不碰系统目录。

本目录是上游便携包的一个**非官方**发行骨架：随包五个插件（详见下文）。
桌面壳启动屏用 DeepSeek 鲸鱼矢量标志 + 「DeepSeek」字标（启动屏不内嵌任何第三方图片素材）。
**发布包已整合离线依赖**：`DSH-portable-work-win-x64.zip` 内自带 pnpm 离线缓存 `store\`，
解压后首启即**纯离线**重建 `node_modules`（`pnpm install --offline --frozen-lockfile`），不需要联网拉取 npm 包。

> 本发行版与 DeepSeek 官方、以及任何其他公司均无隶属或背书关系；发布时可放在**公开仓库**，也可放在私有镜像。

> **English TL;DR** — A self-contained Windows distribution of DeepSeek Harness
> (an extensible LLM agent runtime). This repository holds the *skeleton* (launcher scripts, the WPF
> desktop shell source, dependency manifests + lockfiles, bundled plugin sources and docs). The heavy
> payload (portable Node runtime, offline pnpm store, Tectonic LaTeX runtime, prebuilt `DshDesktop.exe`)
> ships as a **single GitHub Release asset** — one zip that already bundles `store\`. Unpack it once and
> first launch rebuilds `node_modules` purely offline from `store\`. (A `-StoreAsSeparateAsset` split
> stays available for special cases such as the 2 GiB per-asset limit.) MIT licensed; DSH itself is MIT
> by DeepSeek.

---

## 这是什么 / 不是什么

| | |
| --- | --- |
| ✅ | 一套能在任意 Windows 10/11 x64 上跑起来的 **自包含** DSH 环境（自带 Node 运行时、离线依赖缓存 `store\`、Tectonic LaTeX 运行时、桌面壳） |
| ✅ | **离线可重建**：锁文件与清单在仓库里，首启用 `store\` 纯离线执行 `pnpm install --offline --frozen-lockfile` 重建 `app-npm\node_modules` 与 profile 的 `node_modules` |
| ✅ | **可整体搬动**：脚本用相对路径，`DSH_HOME` 在启动时按当前目录推算；换机器/换路径后本地插件的链接会自动重建 |
| ❌ | 仓库本身**不含** Node 运行时 / 离线缓存 / LaTeX 引擎 / 预编译 exe —— 体积过大，走 Release 资产 |
| ❌ | **不是「完全不需要网络」**：调用 LLM API、联网搜索（`dsh-free-search`）、缺失 WebView2 时安装运行时、缺 `pymupdf` 时装它，仍然需要网络 |

版本：`@deepseek-ai/dsh` **0.2.0-rc.2** · `dsh-pdf-reader` **^0.2.0** · `dsh-free-search` **0.6.5** ·
`dsh-computer-use-win` **^0.2.3** · `dsh-latex` **0.2.0**（本地 `link:`）· `dsh-brand` **0.1.8**（本地 `link:`）· Node **v24.19.0** · pnpm **11.19.0**

---

## 资产与仓库内容边界

| | 内容 | 体积 |
| --- | --- | --- |
| **仓库（骨架）** | 启动/停止/回滚脚本、`launcher\` 全部 C# 源码与构建脚本、`app-npm` 与 `dsh-home\profiles\web` 的 `package.json` + `pnpm-lock.yaml`、`plugins\dsh-latex` 源码、文档 | 几 MB |
| **Release 资产（单包）** | 骨架 + `node\`（约 124 MB）+ `dsh-home\runtimes\latex-runtime-win32-x64\`（约 133 MB）+ `DshDesktop.exe`（约 60 MB）+ `store\`（pnpm 离线内容寻址缓存，精简后约 410 MB） | 见 Release 说明 |

**发布形态：默认就是单个 zip。** `tools\pack-portable.ps1` 不带开关就会把 `store\` 一起打进
`DSH-portable-work-win-x64.zip`（两个资产内容合计约 760 MB —— 主包 154 MB + store 604 MB，
远低于 GitHub Release 单资产 2 GiB 上限），
用户**只下一个包、只解压一次**就能纯离线重建依赖。

`-StoreAsSeparateAsset` 仍然保留，但降级为**特殊场景备用**（单包将来逼近 2 GiB，或需要
U 盘分卷 / 分块传输时）：它把 `store\` 从主包排除、单独压成第二个资产，这种模式下两个包
必须解压到**同一层**才能合并。

`.gitignore` 钉了一层：`node\`、`store\`、`vendor\`、`dsh-home\runtimes\`、`DshDesktop.exe`、
各 `node_modules`、`.env` 与凭证文件都不入库；`.gitattributes` 负责换行符一致性
（仓库内 LF，Windows 脚本检出为 CRLF），避免 clone 后 `.ps1` / `.bat` 失效。

---

## 用法一：下载 Release 包（最终用户）

1. 打开本仓库的 **Releases**，下载**一个**资产：`DSH-portable-work-win-x64.zip`
   ——骨架 + `node\` + LaTeX 运行时 + `DshDesktop.exe` + `store\` 离线缓存，全都在这一个包里。
2. **解压一次**到任意目录（例如 `D:\DSH`）：目录里会直接出现 `store\v11\`，不需要再解第二个包。
   （只有维护者选了可选的「拆包模式」时才会多出一个 store 包，那时两个包要解压到同一层。）
3. **校验 SHA256**：`tools\pack-portable.ps1` 打包时会把资产的 SHA256 直接打印到控制台，
   发布者贴进 Release 说明即可（脚本不另外产出校验文件）。对得上再用：
   ```powershell
   Get-FileHash .\DSH-portable-work-win-x64.zip -Algorithm SHA256
   ```
   把输出的 Hash 与 Release 说明里的值逐字符比对，不一致就别解压。
4. 任选一个入口启动：
   - 双击 **`DshDesktop.exe`** —— 原生窗口（内嵌 WebView2），带状态 / 启停 / 版本信息 / 日志 / 余额；
   - 双击 **`start-dsh.bat`** —— 命令行启动，用系统浏览器打开 `http://127.0.0.1:3098`。
5. 首次启动会用包内 `store\` **纯离线**展开依赖（约 2–5 分钟，全程不联网，实测见下文「离线能力与升级注意事项」）。
6. 首启若没有 `app-npm\.env`，会从 `.env.example` 生成模板，把 `DEEPSEEK_API_KEY` 填进去再重启即可。
   本发行版**只使用 DeepSeek 官方模型**：模型路由固定在 `dsh-home\cordis.patch.yml` 的
   `deepseek-official` 路由（`deepseek-flash` / `deepseek-v4-pro`），不预置任何第三方或自建 provider。

> 内嵌浏览器需要系统 **Edge WebView2 运行时**（Win11 与带 Edge 的 Win10 已预装）。
> 缺失时可以继续用 `start-dsh.bat` + 系统浏览器，功能完整。

## 用法二：从仓库重建（开发者 / 维护者）

这条路需要**能访问外部 npm registry**（在可联网的机器或带代理的环境里做即可）：

```powershell
# 1) 取运行时最小集合：官方 Node 便携版 + pnpm（约 124 MB，联网）
powershell -ExecutionPolicy Bypass -File tools\fetch-node.ps1

# 2) 把离线缓存放到包根（从 Release 单包里取 store\，或从已有可用目录复制）
#    store\

# 3) 启动：有 store\ 时首启纯离线重建依赖；没有就联网按锁文件重装
start-dsh.bat
```

组装发布包（把 node + store + LaTeX 运行时 + exe 一起打包）：

```powershell
# 默认（就是发布形态）：一个 zip，store\ 已包含在内
powershell -ExecutionPolicy Bypass -File tools\pack-portable.ps1

# 可选：只组装目录、不压缩（发布前在干净目录做离线自检时用）
powershell -ExecutionPolicy Bypass -File tools\pack-portable.ps1 -NoZip

# 可选、特殊场景：把 store\ 拆成第二个资产（单包将来逼近 2 GiB 时才需要）
powershell -ExecutionPolicy Bypass -File tools\pack-portable.ps1 -StoreAsSeparateAsset
```

桌面壳 `DshDesktop.exe` 是 WPF 自包含单文件程序，需要 **.NET 9 SDK** 自行编译：

```powershell
powershell -ExecutionPolicy Bypass -File launcher\build-desktop.ps1   # 产出根目录 DshDesktop.exe
```

---

## 插件清单

| 插件 | 版本 | 提供的工具 | 依赖 / 运行时 |
| --- | --- | --- | --- |
| `dsh-pdf-reader` | ^0.2.0 | `pdf_scan` / `pdf_read_page` / `pdf_render_region` —— 本地 CPU 的 PDF 内容感知阅读 | **无捆绑运行时**；唯一外部依赖是**系统 Python 3 + `pymupdf`**（缺失只影响 PDF 工具） |
| `dsh-free-search` | 0.6.5 | `web_search` / `advanced_search` / `multi_search` / `platform_search` 等联网检索 | 纯 JS，无运行时；**需要网络**（联网检索后端需可用） |
| `dsh-computer-use-win` | ^0.2.3 | Windows 电脑控制（MCP，工具前缀 `mcp__wincu__*`） | 无（PowerShell + UI Automation） |
| `dsh-brand` | 0.1.8 | 设置 → 品牌：商标/深浅双 logo/尺寸/主色/标语，同一份 `dsh-home\dsh-brand.json` 也驱动桌面启动屏 | `plugins\dsh-brand\` |
| `dsh-latex` | 0.2.0 | `latex_health` / `latex_compile` / `latex_math` —— 自包含 LaTeX | `dsh-home\runtimes\latex-runtime-win32-x64`（Tectonic 0.17.0 + 预热 `minimal`/`chinese`/`common` TeX 资源缓存，约 133 MB）；**本地 `link:` 插件，随 DSH 目录一起升级** |

`dsh-pdf-reader` 的工作方式：PyMuPDF 抽正文文本，再把图 / 表 / 公式区域渲染成高清 PNG
（缓存到会话工作目录的 `.dsh-pdf-reader\`），交给视觉模型用 `read_image` 读——**不是内置 OCR 引擎**。

桌面端的启动画面由 DshDesktop 的启动屏承担（DeepSeek 鲸鱼标志 + 「DeepSeek」字标）。

`dsh-latex` 的引擎不在插件包内，`start-dsh.ps1` 每次启动都会校验 `dsh-home\runtimes\latex-runtime-win32-x64`，
校验失败才需要联网重下（联网不可用时只会临时禁用该插件，其余功能不受影响）。想预装更多宏包：

```powershell
& node\bin\node.exe plugins\dsh-latex\scripts\warm-cache.mjs --profile=chinese
```

---

## 目录结构

以下每一层都已按当前目录核对过：

```
DSH-portable-work-win-x64\
├─ DshDesktop.exe          桌面壳：自包含单文件 WPF + WebView2（发布包内容；仓库里由 launcher\build-desktop.ps1 产出）
├─ app-npm\                核心依赖清单：package.json / pnpm-lock.yaml / pnpm-workspace.yaml
│                             （node_modules 不入库，首启用 store\ 离线重建）
├─ dsh-home\               DSH 主目录（启动时按包内路径推算 DSH_HOME）
│  ├─ profiles\             profile 清单与锁文件
│  │  ├─ web\                Web Profile：五个插件 + cordis.yml / cordis.patch.yml
│  │  └─ headless\           无界面 profile 的清单
│  ├─ runtimes\              插件自带运行时
│  │  └─ latex-runtime-win32-x64\   Tectonic 0.17.0 引擎 + 预热 TeX 资源缓存（约 133 MB，仅 dsh-latex 用）
│  └─ cordis.patch.yml      LLM 路由：只有 DeepSeek 官方（deepseek-official）
├─ plugins\                随包插件源码
│  ├─ dsh-latex\            dsh-latex 0.2.0（本地 link: 依赖，随 DSH 目录一起升级）
│  └─ dsh-brand\            dsh-brand 0.1.8（Web 品牌自定义 + 桌面启动屏共用配置，Apache-2.0）
├─ launcher\               桌面壳源码与构建脚本（.NET 9 / WPF）
│  ├─ DshDesktop\           桌面版源码（WebView2 内嵌界面 + DshDesktop 启动屏）
│  ├─ DshHub\               控制台源码（已存档，不再随包分发 DshHub.exe）
│  ├─ DshCore\              两者共享的路径 / 进程 / 版本 / 更新引擎
│  ├─ IconGen\              图标生成小工具
│  ├─ build-desktop.ps1     构建 DshDesktop.exe
│  └─ build-hub.ps1         构建 DshHub.exe
├─ node\                   Node v24.19.0 + pnpm 11.19.0（Release 载荷；tools\fetch-node.ps1 可重建）
│  └─ bin\node.exe
├─ store\                  pnpm 离线内容寻址缓存（精简后约 410 MB，已包含在发布包里）
├─ tools\                  fetch-node.ps1（取 Node + pnpm）/ pack-portable.ps1（组装发布包）/ pnpm.cmd
├─ docs\                   内部笔记与调研（web-search-backends-research.md）
├─ dist\                   打包输出（pack-portable.ps1 产物，不入库）
├─ start-dsh.ps1 / .bat    启动：环境自检 → 离线重建依赖 → 起服务（端口 3098）
├─ stop-dsh.ps1 / .bat     停止
├─ repair-deps.ps1         只重建依赖，不启动
├─ rollback-dsh.ps1        回滚到升级前备份
├─ update-dsh.ps1          检查 / 升级核心与插件（需要访问外部 npm registry，见「离线能力与升级注意事项」）
├─ dsh.cmd                 headless CLI 入口
├─ plugin-track.json       插件版本清单（桌面端「版本信息」面板与 update-dsh.ps1 共用；本地 link: 插件标 "local": true，界面显示蓝色「本地」标签）
├─ .env.example            API Key 模板（首启复制为 app-npm\.env）
├─ LICENSE / NOTICE        许可证与第三方组件声明
└─ README.md / PORTABLE-RELEASE.md / README.txt
```

---

## 桌面端（DshDesktop）

- `DshDesktop.exe` 是**自包含单文件**，内嵌 WebView2 承载 DSH 界面，无需安装 .NET；首次运行会先解压内置文件到临时目录（慢几秒属正常）。
- **启动屏标志是 DeepSeek 鲸鱼矢量 + 「DeepSeek」字标**：鲸鱼是 XAML 里的矢量 `Path`
  （路径数据抄自上游 `dsh-web-frontend/dist/favicon.svg`，viewBox `0 0 50 50`），随主题自动换色，
  **不内嵌任何第三方图片素材**；背景随系统主题为**纯白或中性近黑**（不用彩色底），强调色（DeepSeek 蓝 `#4D6BFE`）只作点缀；
  标语为两行文本 `Idealism is that you will probably never receive something back,` /
  `but nonetheless still decide to give.`（见 `MainWindow.xaml.cs` 的 `BootPurposeSlogan` 常量）；窗口标题为「DeepSeek Harness」。
- 需要系统 **Edge WebView2 运行时**（Win11 / 带 Edge 的 Win10 已预装）。缺失时用
  `start-dsh.bat` + 系统浏览器即可，功能完整；装 WebView2 本身需要网络。
- 桌面壳只负责界面与调度，实际操作仍走包内 PowerShell 脚本，升级 / 依赖修复逻辑不会与脚本分叉。
- 源码、构建方式与主题机制见 [`launcher/README.md`](launcher/README.md)。

---

## 离线能力与升级注意事项

**这是本发行版的重点，请完整读一遍。**

1. **`store\` 就是离线能力的全部**：pnpm 的内容寻址缓存（`store\v11\...`），精简后约 410 MB。
   仓库里同时固化两个锁文件：`app-npm\pnpm-lock.yaml` 与 `dsh-home\profiles\web\pnpm-lock.yaml`。
2. **新电脑 / 新路径上的首启流程**：`start-dsh.ps1` 发现依赖缺失时，用 `store\` 纯离线执行
   `pnpm install --offline --frozen-lockfile`，重建 `app-npm\node_modules` 与 profile 的 `node_modules`。
   已在全新目录实测：**两条离线安装均成功**，不需要任何网络。
3. **如果 `store\` 不完整**，脚本会打印「内置缓存不完整，将联网补齐缺失依赖」并回退到联网安装。
   这条回退需要能访问外部 npm registry，否则必然失败、首启直接停住 —— 遇到就说明 `store\` 没解压到位，
   重新完整解压发布包（拆包模式则把 store 包补到同一层），而不是重试。同理，`tools\fetch-node.ps1` 也要联网，需要外部网络。
4. **升级方式：整包替换，不用 `update-dsh.ps1`。**
   `update-dsh.ps1` 需要访问外部 npm registry（`https://registry.npmjs.org`）才能工作
   （`-Check` 连不上时会直接报告无法连接并跳过）。本发行版的升级方式是：维护者重新打包并更新 Release，
   用户下载新的 Release 整包替换（或按 [`PORTABLE-RELEASE.md`](PORTABLE-RELEASE.md) 的流程重装）。
   `DSH_NO_UPDATE_CHECK=1` 已在 `start-dsh.ps1` 里默认设置，避免每次启动都去查更新而空等超时。
5. **调用 LLM API 的部分仍然需要联网**（本发行版**只使用 DeepSeek 官方模型**）：
   首启会从 `.env.example` 生成 `app-npm\.env`，填 `DEEPSEEK_API_KEY` 即可；只有所在环境提供等价的
   DeepSeek 官方兼容网关时，才需要用 `DEEPSEEK_BASE_URL` 覆盖默认地址 —— 不预置任何第三方或自建 provider 路由。
6. `dsh-free-search` 的联网检索依赖可用的检索后端；后端不可用时工具仍会挂载，但调用可能失败 —— 这是预期现象，不是包坏了。
7. 依赖已整合在发布包里，因此「出问题时从 Release 重下整包替换」比「就地联网修依赖」更省事、更可复现。

---

## 常见问题

| 现象 | 处理 |
| --- | --- |
| 首启「正在重建依赖」很久（2–5 分钟） | 正常：正在从 `store\` 离线展开几百个包。期间不要关窗口、不要重复双击；进度看 `logs\` 与窗口输出 |
| 首启提示依赖重建失败 | 先确认解压目录里有 `store\v11\`（默认单包解压后就在包根）；若维护者用了可选的拆包模式，两个包要解压到同一层。缺 `store\` 时没法靠联网补齐，把 `store\` 放回去再重试即可 |
| 桌面壳白屏 / 起不来 | 多半是缺 **Edge WebView2 运行时**：改用 `start-dsh.bat` + 系统浏览器，或装 WebView2 运行时（需要网络） |
| 没有 `pdf_*` 工具，或提示缺 pymupdf | 装系统 Python 3 后 `python -m pip install pymupdf`，重启即可。启动日志里有一行现成的依赖状态报告 |
| 没有 `latex_*` 工具 | 引擎校验失败时会只禁用该插件（见 `logs\startup-fallback-latex.patch.yml`）。引擎没法就地重下，需要维护者从 Release 恢复 `dsh-home\runtimes\latex-runtime-win32-x64\` |
| 路径带空格 / 中文，能跑吗 | 脚本内部统一用 `-LiteralPath` 与相对路径解析，一般没问题；但个别第三方原生工具对中文或空格路径敏感，遇到难以定位的报错时，先换到纯英文无空格的短路径（如 `D:\DSH`）复现一下 |
| 整个文件夹搬到别的盘 / 别的机器 | 可以，直接搬。搬动后本地 `link:` 插件的 `node_modules` 链接（junction）可能失效，`start-dsh.ps1` 启动时会按 `package.json` 检测缺失并**自动重建 junction**（打印「已重建本地插件链接」）；确认 `store\` 跟着一起搬过去了 |
| 浏览器打开是 401 | 裸地址会被拒；用核心打印的带 token 地址（`logs\dsh-web.out.log` 里 `dsh web:` 那条），或直接用 `DshDesktop.exe` |
| 端口 3098 被占用 / 提示已在运行 | 脚本检测到端口已监听会直接打开界面；要换端口或彻底重启，先跑 `stop-dsh.bat` |
| 升级后起不来 | `rollback-dsh.ps1 -List` / `-Restore <备份名>` |

---

## 安全

仓库**不含**任何凭据与个人数据，以下内容既不在仓库里、也不在发布包里：

- 凭据：`app-npm\.env`、`dsh-home\.credentials.yaml`；
- 个人会话与附件：`dsh-home\sessions\`、`storages\`、`attachments\`、归档与 usage 记录；
- WebView2 用户数据：`dsh-home\webview2-data\`；
- 语音输入模型：`dsh-home\speech-to-text\`（230 MB，用时按需下载，不随包分发）；
- `node_modules`（由 `store\` 离线重建，不入库、不进 zip）。

`tools\pack-portable.ps1` 打包后会再扫一遍输出目录：发现 `.env` / 凭证类文件会直接中止打包，
疑似 API Key 也会中止。首启会自行生成 `.env` 模板与匿名 ID。

## 文档

| 文件 | 内容 |
| --- | --- |
| [`PORTABLE-RELEASE.md`](PORTABLE-RELEASE.md) | 便携包怎么组装、发布前自检、目标机上的行为、离线/联网边界 |
| [`launcher/README.md`](launcher/README.md) | 桌面壳（DshDesktop）与图标生成、构建脚本说明 |
| [`docs/web-search-backends-research.md`](docs/web-search-backends-research.md) | 联网搜索后端的实测调研（各后端的可达性） |
| [`NOTICE`](NOTICE) | 随包第三方组件与许可证、启动屏标志与标语（含商标与免责说明） |

## 许可证

本仓库以 **MIT** 发布（见 [`LICENSE`](LICENSE)）；随包与 Release 载荷内含的第三方组件见 [`NOTICE`](NOTICE)。
DSH 本体与随包插件遵循各自许可证 ——
上游 [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) 为 MIT。
本发行版与 DeepSeek 官方无隶属或背书关系；启动屏与界面不使用任何第三方的 Logo 或其他图片素材，仅沿用 DeepSeek 蓝 `#4D6BFE` 作界面强调色。
