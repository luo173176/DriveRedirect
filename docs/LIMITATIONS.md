# DriveRedirect 限制与兼容边界

本文档说明哪些场景可以可靠自动重定向、哪些只能辅助或放行，以及为什么。产品不会为了追求覆盖率而破坏安装器逻辑、更新、修复或卸载。

## 1. 根本限制

Windows 没有面向第三方安装器的通用“安装目录重定向”API。安装路径通常由安装器自己的脚本、MSI Directory 表、注册表或运行时逻辑决定。要改变路径，只能使用安装框架公开支持的命令行参数、MSI 属性/Transform，或重新打包安装器。

因此 DriveRedirect 的边界是：

- 不注入安装器进程。
- 不挂接文件系统过滤驱动。
- 不终止并重启安装器。
- 不修改安装器二进制。
- 不对未知 EXE 猜测参数。

进程创建事件（WMI）只用于发现“有安装行为发生”，不能作为文件写入前的可靠拦截点。真正的重定向必须在启动安装器之前完成。

## 2. 启动接管范围

当前实现通过修改以下注册表关联来接管资源管理器双击：

- `HKLM\SOFTWARE\Classes\exefile\shell\open\command`
- `HKLM\SOFTWARE\Classes\Msi.Package\shell\open\command`

可以覆盖：

- 资源管理器中双击 `.exe` 安装包。
- 资源管理器中双击 `.msi`。
- 多数通过 ShellExecute 打开下载文件的浏览器下载项（仍会经过文件关联）。

不能覆盖：

- 在 cmd/PowerShell 中直接执行安装器。
- 软件更新器、游戏启动器、包管理器在内部直接 `CreateProcess` 启动安装器。
- 作为服务或计划任务启动的安装器。
- 用户或第三方工具直接调用 `msiexec`。
- 已经被其他安全软件/沙箱接管的启动链。
- Windows 商店/MSIX 部署。

在这些场景中，后台 WMI 监控可能仍会记录安装器，但不会再修改路径，记录为“未经启动拦截，仅监控”。

## 3. 适配器能力

### MSI / Windows Installer

支持：

- 带有显式公共目录属性（如 `INSTALLDIR`、`INSTALLLOCATION`）的 MSI：通过属性直接指定目标。
- 没有公共属性、但能读取 Directory 表锚点的 MSI：生成 MST Transform，把锚点重挂到 `TARGETDIR` 的 D 盘目录链，并以 `TARGETDIR=D:\` + `TRANSFORMS=...` 启动。

限制：

- 私有 Directory 表 Transform 已在自制 WiX 测试包实测，但不同厂商的目录结构可能不同，兼容性标为 Medium；失败时进入安全放行。
- 按机器安装（per-machine）的 MSI 仍会触发 Windows Installer 自身的 UAC，DriveRedirect 不绕过权限。
- 已安装的同一 ProductCode 不会自动迁移，升级/修复/卸载按原安装位置执行。
- MSI 的卸载/修复依赖 Windows Installer 缓存的包和原始源；如果原始安装源被删除，Windows Installer 可能要求提供源文件，这不是重定向造成的。
- 不支持通过修改 MSI 数据库来违背其签名/自定义操作逻辑；Transform 只重挂目录，不修改文件清单和组件规则。

### Inno Setup

支持：

- 通过 `/DIR="D:\..."` 设置向导默认目录。用户在向导中仍可更改。
- 标准 Inno 安装器通常接受该参数，测试包为 Inno Setup 7。

限制：

- DriveRedirect 不解析 Inno 编译后的脚本，默认目录是根据 PE 架构和软件名推断的；如果安装器的 `DefaultDirName` 使用非标准常量或自定义逻辑，推断值可能与真实默认值不同。
- 64 位 Inno 安装器在 64 位系统上可能使用 `Program Files`，但安装器 EXE 自身是 32 位 PE，当前推断可能落到 `Program Files (x86)`。这不会阻止安装，但目录层级与厂商原始默认值可能不同。
- 如果安装器拒绝 `/DIR`、忽略它或在自定义代码中覆盖路径，安装仍可能写回 C 盘；服务最终验证会标记 `Unverified` 或失败原因。
- 维护/卸载/更新参数会安全放行。

### NSIS

支持：

- 使用标准 `$INSTDIR` 目录页、并接受 `/D=路径` 的 NSIS 安装器。
- `/D` 会追加在参数最后，符合 NSIS 对 `/D` 必须为最后一个参数的要求。

限制：

- 很多 NSIS 安装器使用自定义页面、`SetOutPath` 固定路径、多目录安装或忽略 `/D`；这类安装器无法通用重定向。
- `/D` 路径不能带引号；含空格的路径由 NSIS 自身解析。
- 默认目录可能由 `$PROGRAMFILES64` 等变量在运行时决定；当前原始路径为推断值，可能与真实默认值不同。
- 测试包只验证了标准 `$INSTDIR` + `/D` 场景。

### InstallShield

支持：

- 仅对识别为 Basic MSI 模式的安装器，尝试通过 `/v"INSTALLDIR=\"D:\...\""` 传递 MSI 属性。

限制：

- 没有真实 InstallShield 商业安装包样本用于本次验收；该路径只完成代码与单元级评估，未做真实安装实测，因此兼容等级为 Medium，默认更倾向辅助/放行。
- InstallScript、Pure InstallScript、混合模式和自定义启动器等无法通过通用参数可靠改写，禁止盲目注入。

### 自定义 EXE

支持：

- 只有数据库中标记为 `Verified` 且具有参数模板的安装器配置才允许自动处理。

限制：

- 默认没有内置任何厂商自定义配置；未知 EXE 一律安全放行，不注入任何参数。
- 新建自定义配置需要用户自行验证参数，并承担对应安装器版本变化的风险。

### MSIX / AppX

按 Windows 官方机制处理：

- 不修改包内容、签名或系统管理的安装位置。
- 不强行把 MSIX 安装到 D 盘。
- 兼容性等级 Unsupported，安全放行。

用户可以在 Windows 设置中把新应用的默认安装驱动器设为 D:；这是系统提供的机制，不是 DriveRedirect 的强制重定向。

### 游戏启动器

只能识别常见启动器并进行监控/记录。游戏启动器内部往往自行选择安装目录，甚至先下载再调用其他安装器；没有通用参数可以可靠改写。未验证的启动器不会自动注入参数。

## 4. 路径规则限制

- 目标盘固定为 `D:`，设置接口拒绝切换到其他盘符。
- 默认映射：
  - `C:\Program Files` → `D:\Program Files`
  - `C:\Program Files (x86)` → `D:\Program Files (x86)`
  - `C:\ProgramData` → `D:\ProgramData`
  - `C:\Users\<用户>\AppData\Local` → `D:\Users\<用户>\AppData\Local`（默认禁用）
  - `C:\Users\<用户>\AppData\Roaming` → `D:\Users\<用户>\AppData\Roaming`（默认禁用）
- AppData 不进行全局强制迁移。只有在“允许 AppData 重定向”开关开启、且软件命中应用级白名单和明确映射时才允许。
- 已存在的非空源目录不会被自动迁移或删除。
- 源路径位于非 C 盘时不重定向。
- 目标路径必须位于 D 盘，且不能指向系统保护目录。
- 最长匹配优先；用户自定义规则可以覆盖默认映射。

## 5. 系统保护

默认保护并拒绝重定向：

- `C:\Windows`
- `C:\Windows\System32`
- `C:\Windows\WinSxS`
- `C:\Windows\Installer`
- `C:\Program Files\WindowsApps`
- `C:\Program Files\Common Files\microsoft shared`
- `C:\Program Files\Windows Defender`
- `C:\Program Files\Windows NT`
- `C:\Program Files (x86)\Windows Defender`
- `C:\ProgramData\Microsoft\Windows`
- `C:\ProgramData\Microsoft\Windows Defender`
- `C:\ProgramData\Package Cache`

路径规范化会折叠 `..` 和 `.`，防止通过目录穿越绕到保护目录。系统服务目录、驱动目录、Windows 更新组件和 Microsoft 运行库不会成为重定向目标。

## 6. D 盘不可用

以下情况会停止自动重定向：

- D: 不存在。
- 未就绪（例如 BitLocker 未解锁、磁盘离线）。
- 不是本地固定磁盘。
- 不可写。
- 剩余空间低于设置的最小值（默认 2 GB）。

此时进入辅助模式并提示原因；不会强行把文件写到不可用的目标。

## 7. 更新、修复与卸载

- 不迁移已经安装的软件。
- 不自动移动现有系统文件，不删除原始文件。
- 不修改系统级目录联接。
- 检测到 `/x`、`/uninstall`、`/repair`、`/update` 等维护调用时安全放行。
- 重定向后的软件由安装器自身记录安装位置，正常更新/修复/卸载应继续使用该位置；但部分厂商的更新器会硬编码 C 盘或重新下载完整安装包，这种情况不在通用控制范围内。

## 8. 权限、签名与部署

- 桌面端和服务需要管理员权限；安装器自身如果要求 UAC，仍会显示 UAC。
- MSI 安装包和程序当前未做商业代码签名，SmartScreen 可能提示未知发布者。
- 仅提供 win-x64 自包含发布；ARM64 未验证。
- 未在 Windows 11、Windows Server、域控环境或多用户并发安装场景做完整实机验证。
- 测试覆盖的是可控测试安装包，不代表对所有真实厂商安装器都能自动重定向。

