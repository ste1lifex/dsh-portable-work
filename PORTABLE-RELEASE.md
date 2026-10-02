# DSH Portable · Windows —— 打包与部署说明

本文件写给**维护者**：这份发行版由什么组成、怎么组装、发布前怎么自检、目标机器上会发生什么。

> 最终用户请直接看 [`README.md`](README.md)。

---

## 一、仓库里有什么、Release 里有什么

| | 内容 | 体积 |
| --- | --- | --- |
| **仓库（骨架）** | 启动/停止/回滚脚本、`launcher\` 全部 C# 源码与构建脚本、`app-npm` 与 `dsh-home\profiles\web` 的 `package.json` + `pnpm-lock.yaml`、`plugins\dsh-latex` 源码、文档 | 几 MB |
| **Release 资产（单包）** | 骨架 + `node\`（Node v24.19.0 + pnpm 11.19.0，约 124 MB）+ `dsh-home\runtimes\latex-runtime-win32-x64\`（Tectonic 0.17.0 引擎 + 预热 TeX 资源缓存，约 133 MB）+ `DshDesktop.exe`（自包含单文件，约 60 MB）+ `store\`（pnpm 离线内容寻址缓存，`store\v11\`，已精简） | 见 Release 说明 |

### 单包发布（默认）

`tools\pack-portable.ps1` **不带开关**时产出**一个** `dist\DSH-portable-work-win-x64.zip`，
里面已经包含 `store\`。两个资产内容合计约 760 MB（主包 154 MB + store 604 MB），
远低于 GitHub Release **单资产 2 GiB 上限**，
所以**默认就这么发**：用户只下一个包、只解压一次，首启即可纯离线重建依赖。

| 资产名 | 内容 |
| --- | --- |
| `DSH-portable-work-win-x64.zip` | 骨架 + `node\` + `dsh-home\runtimes\` + `DshDesktop.exe` + `store\` |

### 拆包模式（可选，特殊场景）

`-StoreAsSeparateAsset` 仍然保留，用于单包将来逼近 2 GiB、需要 U 盘分卷或分块传输的场景：
主包排除 `store\`，离线缓存单独成第二个资产。

| 资产名 | 内容 |
| --- | --- |
| `DSH-portable-work-win-x64.zip` | 主包（骨架 + `node\` + `dsh-home\runtimes\` + `DshDesktop.exe`） |
| `DSH-portable-work-win-x64-store.zip` | `store\` 离线缓存 |

**拆包模式下两个资产必须解压到同一个目录**（主包解完再解开 store 包，让 `store\v11\` 落在包根）。

### 当前发布资产与 SHA256

- 资产名与哈希随每次发布更新，**以 GitHub Release 说明为准**（本文件不写死数值，避免与实际包不一致）。
- 仓库可放在**公开仓库**（也可放在私有镜像）：本发行版为 DSH 的非官方 Windows 发行骨架，与 DeepSeek 官方无隶属或背书关系。
- `tools\pack-portable.ps1` **不产出校验文件**，哈希由发布者自己算好、连同资产一起贴进 Release 说明：

  ```powershell
  # 默认单包
  Get-FileHash .\dist\DSH-portable-work-win-x64.zip -Algorithm SHA256
  # 拆包模式（可选）再补一条
  # Get-FileHash .\dist\DSH-portable-work-win-x64-store.zip -Algorithm SHA256
  ```

- 发布说明里至少写清：核心版本（`@deepseek-ai/dsh`）、四个插件版本、Node / pnpm 版本、资产的 SHA256；
  默认单包只需一句「下载一个 zip、解压一次」。

---

## 二、怎么组装一份完整便携包（维护者）

```powershell
# 0) 前置：一台已能跑起来的开发目录（含 node\ store\ dsh-home\runtimes\ DshDesktop.exe）
#    在能访问外部 npm registry / GitHub 的机器上做。

# 1) 桌面壳（在 launcher\ 里，需要 .NET 9 SDK）
powershell -ExecutionPolicy Bypass -File launcher\build-desktop.ps1
#    → 产出根目录 DshDesktop.exe（自包含单文件）

# 2) 组装发布包（默认单包，已含 store\，用户解压一次即可）
powershell -ExecutionPolicy Bypass -File tools\pack-portable.ps1
#    → dist\DSH-portable-work-win-x64.zip   （骨架 + node + 运行时 + DshDesktop.exe + store\；
#                                            顶层目录为 DSH-portable-work-win-x64/，所有条目用 '/' 分隔）
#    打包结束时会直接打印这个资产的 SHA256，复制进 Release 说明即可。
#
#    可选（特殊场景，例如单包将来逼近 2 GiB）—— 拆成两个资产：
#      powershell -ExecutionPolicy Bypass -File tools\pack-portable.ps1 -StoreAsSeparateAsset
#    → dist\DSH-portable-work-win-x64.zip        （不含 store\）
#    → dist\DSH-portable-work-win-x64-store.zip  （store\；条目带 DSH-portable-work-win-x64/store/ 前缀，
#                                                  与主包解压到同一层即合并）
```

`tools\pack-portable.ps1` 支持的开关：

| 开关 | 作用 |
| --- | --- |
| （无） | **默认发布形态**：一个 zip，`store\` 一并入包（用户只下一次、只解一次） |
| `-StoreAsSeparateAsset` | 可选、特殊场景：主包**排除** `store\`，配合单独压缩的 store 资产使用（单包将来逼近 2 GiB、U 盘分卷、分块传输时用；两个包需解压到同一层才能合并） |
| `-NoZip` | 只组装目录、不压缩 —— 发布前在干净目录做离线自检时用 |
| `-OutDir <路径>` | 换输出目录（默认 `dist\`） |
| `-PayloadFrom <路径>` | 从别的目录取载荷（例如上一次的发布构建目录） |
| `-Name <名字>` | 换输出目录名与 zip 名（默认 `DSH-portable-work-win-x64`） |

### 载荷清单

| 载荷 | 是否必需 | 来源 |
| --- | --- | --- |
| `node\` | **必需**（缺 `node\bin\node.exe` 直接报错退出） | `tools\fetch-node.ps1` 生成，或从现成包复制 |
| `store\` | 可选载荷；**默认单包已整合**（缺了目标机首启需联网补齐） | 精简后的 pnpm 离线缓存 |
| `dsh-home\runtimes\latex-runtime-win32-x64\` | 可选（缺了 `dsh-latex` 不可用） | 插件 fetch 脚本下载 + 预热 |
| `DshDesktop.exe` | 可选（缺了只能用 `start-dsh.bat`） | `launcher\build-desktop.ps1` |
| `vendor\` | 可选（当前发行版没有这个目录） | 离线安装器 / pnpm 无法重建的原生产物 |

打包脚本会把仓库骨架复制过去，然后：

- 排除 `.git`、`dist`、`logs`、`backups`、`node\`、`store\`、`vendor\`（大载荷单独按需复制）、
  `dsh-home\runtimes\`（单独复制）、以及各插件 fetch 脚本留下的 `.dsh-runtime\` 下载暂存目录；
- 剔除本机数据与密钥：`app-npm\.env`、`dsh-home\.credentials.yaml`、`.anonymous-user-id`、`pet.json`、
  `sessions`、`storages`、`attachments`、`webview2-data`、`skin-center`、`skins`、`task-board`、
  `dsh-usage`、`dsh-session-archive`、`llm-deepseek`、`.repair-backups`、`speech-to-text`、
  各 `node_modules`、`logs`、`backups`、`dsh.pid`；
- 再扫一遍输出目录：发现 `.env` / 凭证文件或疑似 `sk-...` 形式的 Key 会**中止打包**。

---

## 三、发布前自检（必做）

不要在没验证过的情况下直接上传 zip。推荐流程：

```powershell
# 1) 只组装目录，不压缩（默认单包模式，store\ 会被一起复制进去）
powershell -ExecutionPolicy Bypass -File tools\pack-portable.ps1 -NoZip

# 2) 确认目录里已经有离线缓存
#    → dist\DSH-portable-work-win-x64\store\v11\...
#    （若改用 -StoreAsSeparateAsset 拆包，脚本不会复制 store，需自己把干净的 store\ 放进去）

# 3) 把整个 dist\DSH-portable-work-win-x64\ 复制到**全新路径**
#    （别的盘符或全新目录，确保没有旧 node_modules\.env\logs 残留），然后在那里启动
start-dsh.bat
```

自检要确认的点：

1. 日志里出现 `pnpm install --offline ... --store-dir <解压目录>\store`（脚本按自身位置推导出的**绝对路径**，
   不是相对层级），且**没有**降级到联网安装
   （不应出现「内置缓存不完整，将联网补齐缺失依赖」）；
2. `app-npm\node_modules` 与 `dsh-home\profiles\web\node_modules` 都被重建出来，
   期望是 **reused 数百个 / downloaded 0**；
3. `node\bin\node.exe` 能打印 `v24.19.0`，核心版本是 `0.2.0-rc.2`；
4. 四个插件工具都在：`pdf_*`、`web_search`、`mcp__wincu__*`、`latex_health`；
5. 桌面壳 `DshDesktop.exe` 能起（DshDesktop 启动屏正常、跟随系统深浅主题）；
6. 带 token 的地址 HTTP 200、裸地址 401（认证生效）。

自检通过后，再按第二节打发布包（默认单包），并确认资产小于 2 GiB：

```powershell
Get-ChildItem .\dist\*.zip | Select-Object Name, @{n='GiB';e={[math]::Round($_.Length/1GB,2)}}
```

最后把 zip、SHA256、版本清单贴进 Release 说明（拆包模式下是两个 zip）。

---

## 四、目标机器上会发生什么

1. 解压发布包（默认单包；拆包模式则两个资产解压到同一层），整个文件夹可以随意搬动（脚本用相对路径推算 `DSH_HOME`）。
2. 双击 `DshDesktop.exe`（桌面界面）或 `start-dsh.bat`（浏览器界面）。
3. `start-dsh.ps1` 做自检：
   - `app-npm\node_modules\@deepseek-ai\dsh` 缺失 / 锁文件变化 → 重装核心依赖；
   - `dsh-home\profiles\web\node_modules` 缺少清单里的直接插件依赖 → 从锁文件统一恢复；
   - **有 `store\` → 先跑 `pnpm install --offline --ignore-scripts --frozen-lockfile`；`--store-dir` 由脚本按自身位置推导为 `<解压目录>\store`（绝对路径）**；
     失败或没有 `store\` → 才回退到联网安装（此时需要能访问外部 npm registry）；
   - 本地 `link:` 插件（`dsh-latex`）的 `node_modules` 链接缺失时重建 junction；
   - `dsh-pdf-reader`：无捆绑运行时，只**报告**系统 Python 3 + `pymupdf` 的可用性，不阻止启动；
   - 校验 `dsh-home\runtimes\latex-runtime-win32-x64`，失败才需要联网重下；彻底恢复不了就写一份
     只禁用 `dsh-latex` 的临时补丁，其余功能不受影响；
   - 启动核心（端口 3099，`DSH_NO_UPDATE_CHECK=1`），核心自己打开带认证 token 的地址。
4. 首启若没有 `app-npm\.env`，从 `.env.example` 生成模板并提示填写 `DEEPSEEK_API_KEY`。
5. **升级方式不是 `update-dsh.ps1`**：它需要访问外部 npm registry；本发行版的升级方式是下载新的 Release
   整包替换 —— 维护者重打包并更新 Release，用户整包替换。

### 哪些功能需要联网

| 不需要网络 | 需要网络 |
| --- | --- |
| 启动、会话、文件读写、代码执行、插件加载 | 调用远程 LLM API —— **只走 DeepSeek 官方**（`deepseek-official` 路由，需能访问 DeepSeek 官方 API） |
| 依赖重建（有 `store\` 时） | 没有 `store\` 时的依赖安装（需要访问外部 npm registry） |
| PDF 内容感知阅读（`dsh-pdf-reader`，本地 CPU；前提是系统已装 `pymupdf`） | 装 Python / `pymupdf`（若目标机没有） |
| LaTeX 编译（引擎与缓存已随包，或调用时传 `offline: true`） | 首次装 WebView2 运行时、联网搜索（`dsh-free-search`）、未预热宏包的资源拉取 |

---

## 五、WebView2（桌面壳的内嵌浏览器）

`DshDesktop.exe` 依赖系统的 **Edge WebView2 运行时**。Win11 / 带 Edge 的 Win10 已预装；缺失时：

1. 装微软官方运行时（约 2 MB 引导器，装到当前用户、无需管理员），但这需要网络；
2. 或者干脆用 `start-dsh.bat` + 系统浏览器 —— 功能完整，不依赖 WebView2。

当前发行版**不随包分发** WebView2 安装器（`vendor\webview2\` 不存在）；如果目标机普遍缺运行时，
维护者可以考虑把它加进发布包（`tools\pack-portable.ps1` 已支持 `vendor\`）。

## 六、安全与隐私

打包脚本会剔除（双保险）：`app-npm\.env`、`dsh-home\.credentials.yaml`、`dsh-home\.anonymous-user-id`、
`dsh-home\sessions\`、`dsh-home\storages\`、`dsh-home\attachments\`、`dsh-home\webview2-data\`、
`dsh-home\speech-to-text\`、`dsh-home\dsh-usage\`、`dsh-home\llm-deepseek\`、各 `node_modules\`、
`logs\`、`backups\`、`dsh.pid`。打包后还会扫描输出目录，发现 `.env` / 凭证文件或疑似 API Key 直接中止。

## 七、排障

| 现象 | 处理 |
| --- | --- |
| 首启卡在「正在重建依赖」 | 看 `logs\`；确认 `node\bin\node.exe` 存在、`store\v11\` 存在（`tools\fetch-node.ps1` 只能在联网机器上重建 node） |
| 提示依赖重建失败 | 基本只有一个原因：包根没有 `store\v11\`。默认单包解压后就有；拆包模式要确认两个包解压到同一层，把 `store\` 放回去再重试 |
| 浏览器 401 | 裸地址会被拒；用核心打印的带 token 地址（`logs\dsh-web.out.log` 里 `dsh web:` 那条） |
| 桌面壳白屏 | 大多是 WebView2 缺失/驱动问题：改用 `start-dsh.bat`，或装 WebView2 运行时 |
| 升级后起不来 | `rollback-dsh.ps1 -List` / `-Restore <备份名>` |
| 没有 `latex_*` 工具 | 看启动输出与 `logs\startup-fallback-latex.patch.yml`：引擎恢复失败会只禁用该插件。引擎没法就地重下，从 Release 恢复 `dsh-home\runtimes\latex-runtime-win32-x64\` |
| LaTeX 报 `File 'x.sty' not found` 且开了 `offline` | 该宏包不在缓存里：`warm-cache.mjs --profile=chinese`（或 `full`），或先联网编译一次 |
| 没有 `pdf_*` 工具 | 目标机缺 Python 3 或 `pymupdf`：`python -m pip install pymupdf`（需要网络或离线 wheel） |
| zip 上传被 GitHub 拒 | 单资产超 2 GiB：改用 `-StoreAsSeparateAsset` 拆包，让 `store\` 走第二个资产（默认单包约 760 MB，正常不会触发） |
