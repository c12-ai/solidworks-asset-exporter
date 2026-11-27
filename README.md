# SOLIDWORKS Asset / assembly_package 混合导出插件

这是一个面向 SOLIDWORKS 2025/2026、.NET Framework 4.8、64 位进程的 C# COM Add-in。它把当前装配体拆成四种节点：

术语边界：SOLIDWORKS 当前总装配体在本地和 Wanxiang 中统一称为 `assembly_package`（装配包）。本地顶层目录与 Wanxiang 远端顶层目录均为 `assembly_package/`，上传使用通用接口 `PUT /archive/assembly_package/...`。已有 `settings.json` 兼容配置键 `project_*` 保持不变；装配 XML 根属性使用 `mesh_format`，普通几何叶节点统一写为 `kind="meshes"`。

- `Asset`：当前节点 `is_asset=true|1|yes`。这是硬终止边界，分类扫描不会读取其子节点，也不会在内部发现第二个 Asset。
- `assembly_package geometry`：没有可继续拆分子节点的非 Asset 叶节点，作为装配包中的本地 STEP/STL 几何单元；XML 写为 `kind="meshes"`。
- `Group`：自身不是 Asset，并且仍有可见、未抑制、非包络子节点。保留装配层级和位姿，没有 mesh，并继续向下拆分。
- `Robot`：`is_asset=true` 且 `class=robot` 的硬边界。它不生成 Asset 包或几何，只在启用 assembly_package 导出时以 `kind="robot"` 保留层级、位姿和 `asset_id="<robot-uuid>:<version>"` 引用。

拆分只在 Asset 处停止。非 Asset 装配体无论后代是否包含 Asset 都会继续向下；最终非 Asset 叶节点作为装配包几何单元进入当前 assembly_package 目录，不污染全局 Asset 库。

换机安装和验收见 [Windows 快速安装](#windows-快速安装)。开发分支的测试安装包由 GitHub Actions 生成。

当前源码支持分组、批量和链式绑定，快换架可直接承载移液器等 Tool。每个子实例只绑定一个直接父级。同一可拆件的闭合位和停放位，可用两个同源 CAD 实例保存到不同父级的槽位。多个备选子实例可复用同一物理槽位。含绑定的装配包使用 1.3 格式，记录资产 UUID、槽位标识和最终安装位姿。 [实例承载关系协议](docs/INSTANCE_BINDING_PROTOCOL.md)

当前源码的机器人引用规则以 [Robot Asset 引用协议](docs/ROBOT_ASSET_REFERENCE_PROTOCOL.md) 为准；下方 `v1.0.5` 升版说明保留历史发布行为，不表示旧引用格式仍受支持。

## 当前实现范围

- 两阶段分类；Asset 硬边界短路，非 Asset 装配层级递归拆到叶节点。
- 分类预览通过 Wanxiang `GET /asset/registry` 读取服务端注册信息，再读取装配树、分类属性、Asset 边界内源模型及直接关联图纸。先用源文件 SHA-256 作为快速路径；同一版本的原始文件指纹不同时，下载已注册清单及 STL，并将当前 Asset 临时导出 STL，比对三角面几何与文件级自定义属性。仅重新保存、图纸或文件容器字节变化而几何和发布属性不变时复用原版本，不要求升版；几何或发布属性变化时仍按版本规则处理。预览不打开工程图。同目录同名 SLDDRW 只通过文件系统查找。源文件和已有本地包校验使用持久哈希缓存。本地包缺失时，几何复用路径从 Wanxiang 恢复并校验原始注册包，不用当前文件覆盖旧版本；本地冲突包先备份到 `.local-package-backups`。只有新建 Asset 包时装配体才使用 Pack and Go。
- 分类扫描会过滤隐藏、真正抑制、包络组件；所有分类与业务属性只从模型文件级“自定义”页读取，完全忽略“配置特定”页。`is_asset`、`asset_version`、`assembly_version` 按准确键名和安全缓存读取，不再误用只能提供配置特定属性的组件管理器，也不会因属性求值把模型标成已修改。普通轻量化模型按需隐藏打开、读取后立即关闭；`swx...\VC~~`/`IC~~` 虚拟或内嵌组件绝不独立打开和关闭，只在父装配中临时解析并恢复原轻化状态。结果按源文件缓存，同一文件的重复实例只读取一次。
- Asset 分类边界保持短路：父节点一旦命中 Asset，会在读取 `GetChildren()` 之前返回，不校验子零件/子装配体属性，也不会生成内部节点。源文件收集仅从组件引用读取路径和文档类型，不再为了打包而打开子文档读取元数据。
- UUIDv5、版本规则、文件级必填属性、已保存/无未保存修改校验。分类预览会在计算 Asset 指纹和远端版本之前识别 Asset 根本身是否为虚拟/内嵌组件；这类根不会被当作普通升版模型打开，会明确要求先保存为外部 SLDASM/SLDPRT，并禁止导出。
- Asset UUID v2 使用 SOLIDWORKS 内部创建时间和完整文件名生成稳定 UUIDv5。预览列出版本变更建议，可在导出窗口点击“应用插件建议版本”。MCP 导出自动应用并重新校验。
- STEP AP214 和二进制 Fine STL 双格式导出；模型文档本地原点作为几何原点。
- XML 父子相对位姿、米、`qx/qy/qz/qw`、Asset ID 或 assembly_package 相对 mesh 路径。
- Asset manifest、SHA-256、内容指纹、版本命中复用、同 ID 内容变化拒绝覆盖。
- 只有装配体 Asset 调用 Pack and Go：零件 Asset 只复制自身 SLDPRT，assembly_package 几何单元不调用 Pack and Go。由于部分 SOLIDWORKS 会拒绝 `SetDocumentSaveToNames` 的过滤清单，装配体 Asset 的完整 Pack and Go 结果先写入当前导出事务内的隔离临时目录，随后只把 Asset 根及向下层级内明确收集的外部 SLDASM/SLDPRT 提升到最终 `source/models/`；父装配体、同级分支和上下文外部引用不会进入 manifest 或上传包，临时结果立即清理。`SavePackAndGo` 返回状态数组长度与原始依赖清单不一致时，不再仅凭数量误判失败，而是要求每个外部 Asset 目标文件实际存在后才提交。名称包含 `子组件^所属装配体` 或位于 SOLIDWORKS `VC~~/IC~~` 会话目录的虚拟组件保留在所属 SLDASM 内，不要求生成不存在的独立源文件；Asset 根本身若为虚拟组件则要求先保存为外部文件。
- `class=robot`（忽略大小写和首尾空格）不作为普通 Asset 导出：不参与普通 Asset UUID、内容指纹、版本注册、manifest 或源文件打包，也不生成 STEP/STL。启用 assembly_package 导出时，Robot 保留 `kind="robot"`，仅在 XML 原层级和位姿处写入 `<mesh asset_id="<robot-uuid>:<version>" />`。机器人 UUID 由文件级“设计目的”中的名称确定，版本单独使用正整数 `asset_version`；NAS 上的 URDF 和 meshes 由人工上传，插件不注册或上传这些资源。
- 外部 Asset 组件已加载时会核对 `GetModelDoc2()` 文档路径与组件 SLDASM/SLDPRT 源路径；如果 SOLIDWORKS 返回了父装配文档，插件改为按 Asset 源路径获取正确文档。Pack and Go 前再次校验 Asset 根路径，防止把整机误打包进 Asset。
- 为上述每个 SLDASM/SLDPRT 收集直接关联的 SLDDRW；只复制原始图纸，不生成 PDF，并与 SLDASM/SLDPRT 一起平铺到 Asset 的 `source/models/` 目录。
- Asset 与 assembly_package 独立 staging，成功后目录级提交；既有版本不覆盖。
- 分类预览会完成装配树分类，同时计算 Asset 与 assembly_package 内容指纹：Asset 立即对照 Wanxiang 注册表判断 `asset_version`，assembly_package 立即检查本地相同 UUID/`assembly_version` 的 `export-report.json`。取消“导出 assembly_package”后，不要求 `assembly_version`，也不计算或校验装配包指纹；Asset 预览、导出和注册仍照常执行。Robot 不进入 Asset/assembly_package 几何指纹队列；其 `asset_id` 使用稳定的名称 UUID 加版本号，完整引用参与装配包指纹。任何版本冲突都在预览中显示并禁用“导出”，不会等到导出阶段才首次发现。属性缺失、模型读取、Asset 指纹和 assembly_package 指纹等可继续检查的问题会遍历完成后一次性汇总提示。预览会保留 Asset/assembly_package 指纹以及源文件/打开文档快照；点击导出时不再执行第二次完整分类或重新计算装配包指纹，只校验源文件大小与修改时间、会话内更新标记、活动配置、Wanxiang 最新 Asset 注册表及 assembly_package 本地版本状态，变化时要求重新预览。装配包几何指纹按“唯一源文件+配置”去重，重复装配实例不重复读取同一文件；几何单元直接使用持久 SHA-256 缓存，不调用 Pack and Go。导出窗口显示当前 Asset/assembly_package 阶段并可在单个 SOLIDWORKS 操作结束后取消。
- 导出模型文档当前活动配置及显示状态，不切换配置或显示状态；导出后恢复选择和 STEP/STL 全局设置。
- 几何、Pack and Go 和关联图纸操作统一按需获取模型文档，完成后关闭插件本次打开或临时激活的窗口。装配体引用件不再被强制以只读模式打开；`CloseDoc` 后仍注册在 SOLIDWORKS 时会调用 `QuitDoc`。父装配体可以继续把不可见、非只读的组件文档保留在内存中，这不再被误判为窗口关闭失败；总装以及用户原先已打开的模型窗口保持不变。
- 可选在本地导出成功后直接上传到 [wanxiang-data-service](https://github.com/c12-ai/wanxiang-data-service)：每个 Asset 版本目录打成 ZIP 后，通过 Wanxiang 0.4.0 的 `PUT /asset/{uuid}/v{version}` 单次原子发布，请求头携带 `X-Content-Fingerprint`；本地 assembly_package 通过通用目录接口 `PUT /archive/assembly_package/{path}` 上传。插件不通过通用文件接口写 `assets/` 或注册表；请求使用 Bearer API key 并绕过开发机代理。
- 上传顺序固定为 Asset 原子发布、Wanxiang assembly_package 上传。Asset 发布接口的 201 新注册和 200 幂等复用都视为成功；409 冲突不会留下半发布目录或改动注册表。只有启用“保存本地副本”时，发布完成后才通过 `GET /asset/registry` 下载服务端最终注册表并同步到本地。
- 上传状态逐项显示 `Asset N/M`，Wanxiang assembly_package 分别显示打包、发送、收到 HTTP 响应和校验完成。每次上传都会在 `%LOCALAPPDATA%\SolidWorksAssetExporter\uploads` 创建独立日志，记录请求 URL、HTTP 状态码、耗时和截断后的响应摘要（不记录 API key）；插件界面可通过“查看上传日志”直接打开。上传请求使用完整响应读取，使 30 分钟 HTTP 超时同时覆盖请求体和响应体，避免服务端已处理但客户端无限等待响应结束。

自动测试覆盖文件级资产属性、实例绑定与共享槽位、几何和位姿诊断、版本复用、MCP 协议、万象发布及 SOLIDWORKS 文档生命周期。契约构建使用隔离测试桩，生产安装包由真实 SOLIDWORKS Interop 构建。

已在 SOLIDWORKS Premium 2025 SP5.0 和官方 Interop 33.5.0.53 上验证 COM 加载、MCP 连通、文件级属性双向读写以及完整装配包导出和上传。SOLIDWORKS 2026 兼容性留待现场验收。

## 自定义属性

零件和装配体模板默认将“是否为资产”设为“否”。选“是”后显示资产属性。装配体选“否”时显示“工位属性”，勾选“工位（Workstation）”后填写工位名称。已有旧工位字段的模型，填写新字段时应沿用现有的工位标记和名称。

工位标记仅适用于非 Asset 装配体。导出时会忽略 Asset 和普通零件上残留的工位属性。工位装配体使用文件级 `is_workstation=true|1|yes` 标记，兼容既有模型的 `is-workstation`；名称使用 `workstation_name`，兼容 `workstationname`。两种写法同时存在时必须一致，名称不能为空。分类预览会汇总缺失名称的问题并阻止导出；XML 输出 `kind="workstation"` 和 `workstation_name`，保留工位层级及相对位姿。非 Asset 的普通装配体有子节点时输出 `kind="group"`；空工位仍保留为 workstation，不降为 group 或 meshes。

资产属性协议：

- [Asset Property Protocol（Agent 版）](docs/ASSET_PROPERTY_PROTOCOL_AGENT.md)：确定性的字段值域、属性语义、角色关系和示例。
- [资产属性协议（工程师版）](docs/ASSET_PROPERTY_PROTOCOL.md)：面向工程师的属性含义、资产关系和示例。
- [Robot Asset 引用协议](docs/ROBOT_ASSET_REFERENCE_PROTOCOL.md)：机器人名称 UUID、XML 引用及 NAS 目录解析的跨语言约定。

在 Asset 根零件或子装配体设置 `is_asset=true`。插件在导出时初始化普通资产和装配包版本。Robot 引用的版本仍对应外部 URDF 的发布版本。

插件只读取模型文件级“自定义”页，不读取“配置特定”页；配置级属性无论是否为空、是否与文件级同名，均不参与分类、版本判断或 manifest。父节点已是 Asset 时，子节点属性完全不参与分类或 manifest 校验。Asset manifest 只保存 Asset 根文件级自定义属性。

Agent 可通过 MCP 新增并保存承载关系、维护文件级接口和槽位、预览及导出资产。普通资产版本和装配包版本由插件根据发布状态和内容指纹维护。连接方法和工具说明见 [MCP 使用指南](docs/MCP.md)。

接口候选由 Wanxiang 在线目录集中维护。接口库补充包另提供 103 个按 `类别.规格[.尺寸/型号]` 命名的基础候选。更新现有属性模板可使用 `Install-InterfaceOptions.ps1`。详见 [接口库与模板说明](docs/INTERFACE_CATALOG_GUIDE.md)。

### Asset 属性填写说明

下表属性使用模型文件级“自定义”页。必填项取决于资产角色和承载关系。普通 Asset 的 `零件名` 和 `设计目的` 缺失或为空白时，分类预览会在完成全部可执行检查后统一列出并禁止导出。

| 属性 | 类型/格式 | 填写说明 |
| --- | --- | --- |
| `零件名` | 文本 | Asset 必填名称；必须位于文件级“自定义”页且不能为空。 |
| `is_asset` | 布尔值 | 是否把当前零件或子装配体作为 Asset。填写 `true`、`1` 或 `yes`。Asset 是硬边界，分类扫描不会继续读取其内部节点。 |
| `class` | 单选枚举 | Robot 使用 `robot`；发布到 Wanxiang `assets/` 的普通 Asset 只能使用 `movable`、`station`、`structure`。旧值 `moveable`、`equipment` 会在分类预览中报错。 |
| `is_tool` | 布尔值 | 是否作为机器人使用的工具。Tool 本身也可以属于 `movable`，例如在快换过程中由机器人 attach；Tool 还需要在后续定义 TCP Point。建议统一填写 `true` 或 `false`。 |
| `is_fixture` | 布尔值 | 是否具有定位、夹持、承载或接收其他 Asset 的治具功能。`movable`、`station` 和 `structure` 都可以同时是 Fixture。 |
| `is_quick_changer` | 布尔值 | 是否为快换盘。快换盘仍使用既有 `class`，不新增 quick-changer class。选中后必须填写 `quick_changer_side`。 |
| `quick_changer_side` | 枚举 | `robot_side` 表示快换盘-上/机器人端；`tool_side` 表示快换盘-下/工具端。上下盘是两个独立 Asset，并各自拥有 UUID 和版本。 |
| `is_quick_changer_rack` | 布尔值 | 是否为快换支架。快换支架不能由机器人搬运但需要参与快换交互，必须是 `class=station`，并通过 `accepts_interfaces` 声明可停放接口。 |
| `connection_interface` | 文本 | 当前 Asset 连接到父级 Asset 时提供的单个接口。多个可接受接口应填写到 `accepts_interfaces`。 |
| `accepts_interfaces` | 文本列表 | 当前 Asset 可以接收的子级机械接口或工件接口；多个值使用英文分号 `;` 分隔。父级的该字段必须包含子级的 `connection_interface`。 |
| `slots_num` | 非负整数 | 由插件写入的总槽位容量，包含空槽。`0` 表示无槽位。 |
| `slot_type` | 固定下拉项 | 仅在 `slots_num > 0` 时定义。可选 `place`、`mount`、`clamp`、`thread`、`press_fit`、`snap_fit`。 |
| `engagement` | 固定下拉项 | 资产设计接合方式。`loose` 允许设计内的相对运动，`tight` 保持局部相对位姿。无槽资产也可填写。 |
| `is_adjustable` | 布尔值 | Asset 的安装位姿或空间布局位置是否允许调整。`movable` 通常不可调，`structure` 可以根据布局需要设为可调。 |
| `can_hold_contents` | 可选布尔值 | 是否直接容纳无需 CAD 模型的内容物。文件级属性使用 `1` / `0`，资产 manifest 顶层输出 JSON 布尔值，缺省为 `false`。 |
| `has_QRcode` | 布尔值 | 当前 Asset 是否需要二维码标识。Property Tab Builder 模板使用 `1`/`0`；选中后显示二维码数量、尺寸和间距字段。 |
| `QR_num` | 非负整数 | 当前 Asset 上的二维码数量。没有二维码时填写 `0`；启用 `has_QRcode` 时应填写大于 `0` 的数量。 |
| `QR_size` | 文本 | 单个二维码的实体尺寸，通常指正方形边长。当前模板没有规定单位或格式，项目内必须统一约定并保持一致。 |
| `QR_spacing` | 非负数 | 多个二维码之间的间距。当前模板没有规定长度单位，也没有规定按边缘还是中心测量，项目内必须统一约定；只有一个二维码时填写 `0`。 |
| `asset_version` | 正整数 | Asset 内容版本，从 `1` 开始。实际几何或发布的文件级自定义属性改变时应提升版本；单纯重新保存模型或修改图纸而不改变这两者时不要求升版。已发布的同版本包不能覆盖。`class=robot` 时直接作为 `asset_id` 冒号后的版本号并对应 NAS 的 `vN` 目录。 |
| `设计原理` | 文本 | 说明该 Asset 实现功能所采用的机械、电气或控制原理。 |
| `设计目的` | 文本 | 说明设计该 Asset 要解决的问题、目标和预期用途；`class=robot` 时填写机器人名称，例如 `Hebe`，按 Robot Asset 引用协议生成稳定 UUID。该名称必须与 NAS 机器人目录名按同一规则规范化后的结果一致。 |
| `升版说明` | 文本 | 说明当前版本相对上一版本的修改内容和升版原因；初始版本可填写“初始版本”。 |

`class` 的建议含义：

- `movable`：机器人能够移动、抓取或转移的物体，通常设置为 `is_adjustable=false`。后续必须为其定义供 Tool attach 的抓取 Point。Tool 本身也可以属于 `movable`。
- `robot`：执行机构。
- `station`：整体固定、机器人不会搬运，但机器人会与其发生放置、取出、定位、加工或换装交互的 Asset，例如离心机、固定工位、支架适配器或快换支架。固定工位声明机械 `accepts_interfaces` 时，也要按真实的安装面、定位基准或紧固方式定义 `connection_interface`。
- `structure`：机器人不能移动且不需要与之交互的结构，例如框架、护栏或纯支撑结构；可以设置 `is_adjustable=true`，表示其空间布局位置允许调整。

以上属性只描述机械工程师在设计阶段能够直接确定的 Asset 分类和接口能力。Position、Area、抓取 Point、TCP Point、设备交互 Point 等空间定义不在当前 Property Tab 中填写，后续直接定义在资产数据中。

二维码字段目前只作为 Asset 自定义属性写入 manifest。插件不会生成二维码内容、二维码图片或打印文件，也不会校验 `QR_num`、`QR_size`、`QR_spacing` 之间的业务关系。填写时建议遵循以下约定：

- `has_QRcode=0`：`QR_num=0`、`QR_spacing=0`，`QR_size` 留空。
- `has_QRcode=1`：`QR_num` 填写大于 `0` 的数量，并填写 `QR_size`；数量大于 `1` 时再按项目统一口径填写 `QR_spacing`。
- 尺寸和间距的单位、测量基准必须由项目统一约定；当前模板和导出程序不会自动换算。

> 当前零件和装配体模板均使用 `QR_num` 保存二维码数量。已用旧模板写入的 `号数30` 会保留在原模型和导出属性中。

条件填写约定：

- `is_tool=true` 时必须填写 `connection_interface`；工具可直接安装到 Robot，也可通过成对快换盘安装。
- `is_fixture=true` 时可按需填写 `slots_num`。仅标记为 Fixture 且没有承载关系时，接口可留空。
- `is_quick_changer=true` 时必须同时填写 `quick_changer_side`、`connection_interface` 和 `accepts_interfaces`。
- `is_quick_changer_rack=true` 时必须使用 `class=station` 并填写 `accepts_interfaces`。
- `class=robot` 的 `accepts_interfaces` 可留空。Robot 不能同时标记为 Tool、Fixture、快换盘或快换架。
- 接口名称采用精确匹配并描述真实机械兼容性。螺栓法兰使用明确的孔数、螺纹和分度圆信息，例如 `法兰-4xM6-PCD30`；容纳类接口使用对象类型和决定兼容性的几何特征，例如 `试管-D20`，不单独使用容量名称。快换盘上下盘之间使用独立的快换配对接口名称。
- `is_adjustable=true` 只表示安装位姿可调，不表示 Asset 的所有机械或工艺参数均可调。

安装角色由 `class` 和上述 `is_` 属性自动推导，不额外填写 role。业务规范要求连接同时满足“父级 `accepts_interfaces` 包含子级 `connection_interface`”和角色矩阵。允许的机械链路为 Robot→Tool、Robot→快换盘-上、快换盘-上→快换盘-下、快换盘-下→Tool、快换架→快换盘-下、快换架→Tool；禁止 Robot→快换盘-下、快换盘-上→Tool 和快换盘-下→快换盘-上。显式承载关系逐边校验接口、角色、单父级和无环约束。CAD 树保留原装配层级，机械承载关系单独保存。Robot 不作为本功能的关系端点。未使用的工具端快换组件应停放在快换架上。

填写示例：

| Asset | `class` | 关键属性 |
| --- | --- | --- |
| Robot | `robot` | `accepts_interfaces=法兰-4xM6-PCD30`；`设计目的=Hebe`；`asset_version=1` |
| 快换盘-上 | `movable` | `is_quick_changer=1`；`quick_changer_side=robot_side`；`connection_interface=法兰-4xM6-PCD30`；`accepts_interfaces=快换盘-A` |
| 快换盘-下 | `movable` | `is_quick_changer=1`；`quick_changer_side=tool_side`；`connection_interface=快换盘-A`；`accepts_interfaces=法兰-4xM6-PCD30` |
| 转移夹指 Tool | `movable` | `is_tool=1`；`is_fixture=1`；`connection_interface=法兰-4xM6-PCD30`；`accepts_interfaces=压滤瓶夹持接口` |
| 快换支架 | `station` | `is_fixture=1`；`is_quick_changer_rack=1`；永久固定时 `connection_interface` 留空；`accepts_interfaces=快换盘-A`；`slots_num=2` |
| 10 ml 试管 | `movable` | `connection_interface=试管-D20` |
| 固定试管架/支架适配器 | `station` | `is_fixture=1`；永久固定时 `connection_interface` 留空；`accepts_interfaces=试管支架-A` 或 `试管-D20` |

插件使用 `is_asset` 进行分类。普通资产及装配包缺少有效版本时从 v1 开始校验，插件在导出时保存最终版本。Robot 引用为 `asset_id="<robot-uuid>:<asset_version>"`，UUID 由文件级“设计目的”中的机器人名称生成。分类预览检查必填属性、布尔格式、快换侧别和角色所需接口，通过后将普通资产属性写入 manifest。

同一 `uuid + asset_version` 的原始指纹一致时直接复用；原始指纹不同但实际 STL 几何及文件级自定义属性语义一致时，也复用 Wanxiang 已注册的原始包。比较属性时，未设置与空文本等价；`is_tool`、`is_fixture`、`is_quick_changer`、`is_quick_changer_rack`、`is_adjustable`、`can_hold_contents`、`has_QRcode` 的未设置、`0`、`false`、`no` 统一视为 false。若几何或非默认的发布属性变化，则拒绝用同一个版本号覆盖，必须提升 `asset_version`。Asset 注册信息以 Wanxiang 注册表为准；本地 Asset 地址由输出根目录、UUID 和版本确定，每个版本目录中的 manifest 是该 Asset 的文件与哈希记录。

Asset UUIDv5 v2 的输入严格限定为 SOLIDWORKS 内部创建时间和文件名（含扩展名）。绝对路径、配置、显示状态、文件内容和 `asset_version` 不参与 UUID，因此移动目录、切换配置或正常升版不会改变资产身份；重命名文件或内部创建时间不同会得到新 UUID。同一源文件的多个装配实例保留各自 XML 节点和位姿，但共享同一个 `asset_id`，Asset 包只创建或复用一次。`asset_id` 是 `<uuid>:<version>`，只出现在装配 XML；manifest 内只保存独立的 `uuid` 和 `version`。v1.0.5 起使用 v2 身份前缀，因此更早插件生成的 UUID 不会与新规则混用；旧注册项保留但不会被误认为新身份的当前版本。

Robot 使用独立的 UUIDv5 名称规则：namespace 为 `6ba7b811-9dad-11d1-80b4-00c04fd430c8`，哈希名称为 `urn:solidworks-robot:v1:` 加规范化后的机器人名称，不添加长度编码。规范化依次执行协议固定的 Unicode `White_Space` 首尾裁剪、NFC、仅 ASCII `A-Z` 转小写；非 ASCII 字母保留大小写。版本不参与哈希，升版只改变 `asset_id` 冒号后的正整数。普通 Asset 的 UUID 规则保持上述 v2 算法。

既有 Asset 的 `content_fingerprint` 仍沿用已注册 SHA-256，不重写远端历史版本。原始指纹先按 Asset 边界内源模型的文件名、长度、SHA-256 和根模型身份种子计算，再计入直接关联图纸；这只是快速命中条件，不再单独作为“必须升版”的依据。不命中时，比对当前临时导出的 STL 与远端清单所列 STL：忽略 STL 文件头、法线、面顺序及顶点顺序，只比较三角面坐标；同时比对 manifest 与当前文件级自定义属性。两者一致即复用原始指纹和原始包。不能取得或校验远端几何时停止预览检查，不擅自要求升版或覆盖远端版本。

原始指纹命中时，预览无需打开每个模型或工程图；未命中时，仅对需要语义复核的 Asset 临时激活源模型并导出 STL，不打开工程图。文件 SHA-256 缓存在当前用户 `%LOCALAPPDATA%/SolidWorksAssetExporter/asset-file-hashes.json`，缓存键包含完整路径、文件大小和最后修改时间；缓存损坏或无法写入时只会退化为重新计算，不会阻断预览或导出。

绝对目录、Asset 在父装配体中的实例位姿和配合、父装配体边界外的修改、上传地址和注册表时间戳不参与 Asset 版本判断。原始指纹不一致时，插件使用 STL 做语义复核。manifest 校验所有原始导出文件的大小和 SHA-256。已发布资产的实际几何或发布属性变化时，插件计算并写入新版本。

### Property Tab Builder 模板与属性同步工具

仓库提供以下模板：

- `templates/custom-properties/part.prtprp`：零件。
- `templates/custom-properties/asem.asmprp`：装配体。
- `templates/custom-properties/draw.drwprp`：工程图。
- `templates/custom-properties/properties.txt`：SOLIDWORKS 属性名称候选清单。

零件和装配资产的属性面板按资产角色、接口与槽位、二维码和设计说明排列。槽位类型与接合方式使用固定下拉选项。插件与原生面板共用文件级自定义属性，插件仅在 `slots_num > 0` 时显示槽位类型。 [属性面板行为](docs/INTERFACE_CATALOG_GUIDE.md#模板修改范围)

`scripts/custom-properties.schema.csv` 是属性预览/同步脚本及 VBA 宏使用的保留字段清单。批量同步会保留插件维护的接口、槽位和版本属性，其余文件级属性按 CSV 添加或删除。应用前先检查预览清单。

先运行只读预览并检查删除/新增清单，再决定是否应用：

- PowerShell 入口：`scripts/Preview-CustomProperties.cmd`。
- VBA 预览宏：`macros/PreviewCustomProperties.swb` 或 `macros/PreviewCustomProperties.bas`。
- VBA 应用宏：`macros/ApplyCustomProperties.bas`。应用前会检查文件状态并创建备份，但仍应人工确认预览结果。

旧接口属性需要单独迁移，不能仅通过安装新模板完成：

- `macros/MigrateFixtureAcceptsInterfaces.swb`：对 `is_asset=1` 且 `is_fixture=1` 的 Asset，把文件级 `accepts_interface` 复制到空白的 `accepts_interfaces`。
- `macros/MigratePlacementToConnectionInterface.swb`：对 `is_asset=1` 且 `is_placement_required=1` 的 Asset，把文件级 `placement_interface` 复制到空白的 `connection_interface`。

两个宏都会先完整扫描、检查冲突并备份目标 SLDASM/SLDPRT；新属性已有不同值时停止且不覆盖，不删除旧属性，也不修改 Asset/assembly_package 版本或配置特定属性。

## 输出

“Asset 本地输出根目录”保存可跨 assembly_package 复用的 Asset 版本包；“assembly_package 本地输出根目录”应选择名为 `assembly_package` 的顶层目录，用于保存当前 SOLIDWORKS 总装配体的 XML、STEP/STL 和导出报告。历史设置若以 `project` 或 `projects` 结尾，插件会自动迁移为同级的 `assembly_package`。勾选“导出 assembly_package”时两个根目录必须分开，防止全局 Asset 库与一次性装配包结果互相覆盖；关闭后装配包根目录可以留空，只导出/上传非 Robot Asset。

```text
asset-library/
  asset-registry.json
  <asset-uuid>/v<asset-version>/
    asset_<uuid>_v<version>.json
    geometry/model.step
    geometry/model.stl
    source/models/             # SLDASM/SLDPRT/SLDDRW

assembly_package/
  <assembly-uuid>/v<assembly-version>/
    assembly_<assembly-uuid>_v<version>.xml
    meshes/<project-unit-uuid>/model.step
    meshes/<project-unit-uuid>/model.stl
    export-report.json
```

assembly_package 几何单元永远同时生成 STEP 和 STL；窗口中的格式选项只决定 XML 的 `mesh file` 引用哪个格式。装配包不生成 SLDPRT/SLDASM、SLDDRW、PDF 或 Asset manifest。
Robot 节点不属于 assembly_package 几何单元，因此不会出现在装配包的 `meshes/` 中；开启 assembly_package 时在 `kind="robot"` 节点中写 `mesh asset_id="<robot-uuid>:<version>"`。

## Wanxiang 数据服务与注册表

分类预览默认直接读取 Wanxiang 注册表，因此无论是否勾选“本地导出成功后直接上传”，都需要填写：

- 数据服务地址：填写团队提供的万象数据服务地址。
- API key：不写入插件 `settings.json`，单独保存在当前 Windows 用户的 `~/.config/wanxiang/data-service-api-key`。
- 保存本地副本：默认关闭；启用后才把本次读取或上传后的 `asset-registry.json` 同步到 Asset 资产库根目录。

远端目录布局由插件固定，界面和配置文件均不能修改：

```text
assets/
  asset-registry.json
  <asset-uuid>/v<asset-version>/...
assembly_package/
  <assembly-uuid>/v<assembly-version>/...
```

分类预览固定调用 `GET /asset/registry`；注册表尚不存在时服务返回逻辑空表及 `X-Registry-Exists: false`。每个 Asset 版本通过 `PUT /asset/{uuid}/v{version}` 上传完整 ZIP，并在 `X-Content-Fingerprint` 中发送 manifest 指纹；服务端校验 manifest、必需文件、大小和 SHA-256 后一次性提交版本目录与注册表。本地 assembly_package 使用 `PUT /archive/assembly_package/...` 上传到同名远端顶层目录。网络或服务端失败不会删除本地导出结果；相同包可幂等重试，409 冲突必须重新预览或修改 `asset_version`。

Robot 不调用 `PUT /asset/{uuid}/v{version}`，不进入普通 `assets/` 注册表。它只作为 assembly_package XML 中 `kind="robot"` 节点的 `asset_id="<robot-uuid>:<version>"` 引用；本地和上传后的 XML 内容保持一致。

机器人资源人工上传到 NAS，目录保留原始名称。示例布局：

```text
robots/
  Hebe/
    v1/urdf/
    v2/urdf/
    meshes/
```

URDF 自己引用 `robots/Hebe/meshes/` 中的网格；消费者不能把它改为 `robots/<uuid>/...` 或假定 meshes 位于版本目录。UUID 不可逆，消费者须扫描 `robots/` 的直接名称子目录，使用同一算法建立 UUID 到原始名称目录的索引，再定位 `vN/urdf/`。新增、改名或删除目录后必须刷新缓存；重复规范化名称、未知 UUID、缺失版本必须报错。插件不连接 NAS 校验这些资源，也不代表 Wanxiang 消费端已部署解析支持。

仓库提供 [Python 参考实现](scripts/robot_asset_reference.py)，在仓库根目录执行，`ROBOTS_ROOT` 替换为实际 `robots/` 目录：

```sh
python3 scripts/robot_asset_reference.py uuid Hebe
python3 scripts/robot_asset_reference.py index ROBOTS_ROOT
python3 scripts/robot_asset_reference.py resolve ROBOTS_ROOT 4ce608df-8e9e-5a8b-b084-894b8da37409:1
```

完整规范及跨语言测试向量见 [Robot Asset 引用协议](docs/ROBOT_ASSET_REFERENCE_PROTOCOL.md)。更新前已有含 Robot 的装配包需要提升 `assembly_version` 后重新导出；不保留旧机器人引用格式。

XML 示例：

```xml
<assembly schema_version="1.0" uuid="..." version="3"
          mesh_format="step" length_unit="m" quaternion_order="xyzw">
  <nodes>
    <node id="..." parent_id="" name="Tooling-1" kind="group">
      <pose tx="0" ty="0" tz="0" qx="0" qy="0" qz="0" qw="1" />
    </node>
    <node id="..." parent_id="..." name="Motor-1" kind="asset">
      <pose tx="0.1" ty="0" tz="0" qx="0" qy="0" qz="0" qw="1" />
      <mesh asset_id="<uuid>:2" />
    </node>
    <node id="..." parent_id="..." name="Fixture-1" kind="meshes">
      <pose tx="0" ty="0.2" tz="0" qx="0" qy="0" qz="0" qw="1" />
      <mesh file="meshes/<project-unit-uuid>/model.step" />
    </node>
    <node id="..." parent_id="..." name="Robot-1" kind="robot">
      <pose tx="0" ty="0" tz="0" qx="0" qy="0" qz="0" qw="1" />
      <mesh asset_id="4ce608df-8e9e-5a8b-b084-894b8da37409:1" />
    </node>
  </nodes>
</assembly>
```

Asset、装配包几何节点都是叶节点；Group 没有 mesh。装配包几何节点统一序列化为 `kind="meshes"`，不产生 `kind="project"` 或单数 `kind="mesh"`。XML 不输出 joint、轴或关节类型。混合拆分时至少要有一个可见、未抑制、非包络的固定顶层组件，并把固定组件优先写入节点序列，但不会把真实位姿归零。

### 装配包上传后自动提交解析

同时启用装配包导出和导出后上传时，可以勾选“上传装配包后自动提交解析”。此选项默认关闭。填写“解析服务地址”和“装配提供方令牌”，再在“Wanxiang 项目”中选择“项目编号”或“项目名称”，填入与现有项目匹配的值。解析服务地址单独配置。提供方令牌与数据服务 API key 分开保存，由当前 Windows 用户的 DPAPI 加密。

装配包上传成功后，插件调用解析服务的 `POST /api/v1/integrations/assembly-packages/events` 提交 v2 事件。请求携带装配 UUID、版本、项目和 ZIP 内 `export-report.json` 原始字节的 SHA-256。相同装配版本、报告和项目选择会生成相同的 `event_id`，临时网络或服务故障时会用同一事件有限重试。

HTTP `202` 表示解析任务已受理。导出窗口会显示任务 ID 和事件 ID，解析由服务端后台执行。通知失败时，导出窗口分别显示上传成功和解析提交失败，上传日志保留失败信息。

## 构建与测试

机器人 UUID 和 NAS 参考解析器另有 20 项 Python 标准库测试（Python 3.8+，无需第三方依赖），已接入 CI：

```sh
python -B -m unittest discover -s tests -p test_robot_asset_reference.py -v
```

正式发布按以下顺序执行。核心测试和契约验证不需要安装 SOLIDWORKS；生产构建使用仓库内的官方 Interop：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-core.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\verify-addin-contract.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\build-addin.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\new-release-package.ps1 -Version v1.0.5
```

`verify-addin-contract.ps1` 使用 `InteropStubs.cs`，只验证没有 SOLIDWORKS 的代码契约；输出固定隔离到 `bin\Contract-<Configuration>`，不得作为安装 DLL。`build-addin.ps1` 才会生成可由 SOLIDWORKS 加载的正式 `bin\Release`。打包脚本会检查 Add-in 必须引用真实的 `SolidWorks.Interop.sldworks`、`SolidWorks.Interop.swconst` 和 `SolidWorks.Interop.swpublished`，并拒绝任何在 DLL 内嵌入测试桩接口的构建。

`third_party\solidworks` 必须包含：

- `SolidWorks.Interop.sldworks.dll`
- `SolidWorks.Interop.swconst.dll`
- `SolidWorks.Interop.swpublished.dll`

## Windows 快速安装

需要 Windows 上的 64 位 SOLIDWORKS、.NET Framework 4.8 和 Codex 桌面应用或 CLI。本项目已在 SOLIDWORKS 2025 SP5.0 验证加载和导出。

1. 在有目标分支的 GitHub 仓库打开 **Actions → CI**，选择对应提交的成功运行，下载 `SolidWorksAssetExporter-package-<运行编号>`。解开 Actions 下载包，再完整解压其中的 `SolidWorksAssetExporter-*.zip`。正式版本见 [GitHub Releases](https://github.com/c12-ai/solidworks-asset-exporter/releases)。换机验证当前功能时，确认解压目录含 `Install-Codex.cmd`。
2. 完全退出 SOLIDWORKS。右键 `Install.cmd`，选择“以管理员身份运行”。Windows 插件和 MCP 程序默认安装到 `%ProgramData%\SolidWorksAssetExporter`。
3. 切回平时使用 SOLIDWORKS 和 Codex 的 Windows 用户，直接运行 `Install-Codex.cmd`。它注册 `solidworks-assets` MCP 并安装 `solidworks-asset-exporter` 技能。若实际使用的是单独的当前用户更新版，脚本会根据 COM 注册定位对应的 MCP 程序。
4. 启动 SOLIDWORKS 并打开一份已保存的测试总装，完全重启 Codex。在安装包目录运行 `.\Install-Codex.cmd -Check -ProbeSolidWorks`，核对配置、技能和当前总装连接。再在 Codex 中调用 `solidworks-assets` 的 `assembly_context`，返回当前总装路径和活动配置后即可用 `asset_list` 检查资产属性。 [MCP 配置与排障](docs/MCP.md)

安装原生属性模板时，在 SOLIDWORKS 的“系统选项 → 文件位置 → 自定义属性文件”确认目标目录。在安装包目录运行以下命令，将示例路径换成该目录。执行前会备份已有模板，安装后在属性任务窗格按 `F5` 刷新。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-CustomPropertyTemplates.ps1 -TargetDirectory 'C:\CAD\CustomProperties' -Apply
```

先用测试装配验证本地分类、槽位和导出。在 `Asset / assembly_package 导出` 窗口设置本机输出目录。需要核对万象版本或上传时，再填写团队的数据服务地址和 API key，凭据存到当台电脑的当前 Windows 用户账户。普通上传保持“上传装配包后自动提交解析”关闭。 [发布与通知说明](#装配包上传后自动提交解析)

从源码安装时，先运行 `build-addin.ps1` 和 `new-release-package.ps1` 生成 ZIP，再按相同步骤安装。卸载前关闭 SOLIDWORKS，以管理员身份运行 `Uninstall.cmd`。如需同时注销 Codex 的 MCP，在当前用户下运行 `codex mcp remove solidworks-assets`。插件设置位于 `%APPDATA%\SolidWorksAssetExporter`，卸载会保留设置和导出数据。 [构建与测试](#构建与测试)

### 插件未出现在“工具”菜单

在“工具 → 插件”确认 `Asset / assembly_package 混合导出` 已勾选。加载日志位于 `%LOCALAPPDATA%\SolidWorksAssetExporter\addin.log`，成功加载时可见 `ConnectToSW succeeded.`。更新机器级插件前应先完全退出 SOLIDWORKS。

## v1.0.5 历史变更

本节记录 `v1.0.5` 的历史发布行为；机器人引用现已更新，当前源码以 [Robot Asset 引用协议](docs/ROBOT_ASSET_REFERENCE_PROTOCOL.md) 为准。

- SOLIDWORKS 本地整机导出统一命名为 `assembly_package`（装配包），工具菜单、导出窗口、分类预览、错误提示和本地报告说明同步更新。
- Wanxiang 顶层目录由 `projects/` 改为 `assembly_package/`，上传目标同步改为 `PUT /archive/assembly_package/...`；已有 `settings.json` 兼容配置键保持不变。
- XML 文件名前缀保留 `assembly_`，根属性使用 `mesh_format`，普通几何节点使用复数 `kind="meshes"`。由于装配包 XML 契约已变化，已有相同 UUID 的旧内容需要提升 `assembly_version` 后重新导出。

- 新增快换盘、快换架及统一安装接口属性；不新增 class，角色由 `class`、`is_tool`、`is_fixture`、`is_quick_changer`、`quick_changer_side`、`is_quick_changer_rack` 推导。
- 分类预览一次性校验快换侧别、布尔格式及各角色必填接口；仍只读取文件级“自定义”属性。
- 发布包新增 `custom-properties/` 与 `Install-CustomPropertyTemplates.ps1`，可备份并安装 SOLIDWORKS 属性模板。
- 删除已废弃的 `accepts_robots`；机器人与 Tool 的机械兼容关系只通过标准接口匹配。
- 发布包新增旧接口迁移宏：`accepts_interface` → `accepts_interfaces`、`placement_interface` → `connection_interface`。

- 同步 Wanxiang 0.4.x：Asset 使用单次 ZIP 原子发布；assembly_package 使用 `/archive/assembly_package/...` 进行目录 ZIP 上传，发布冲突不会留下半成品或覆盖远端数据。
- Asset UUID 升级为 v2 规则，仅由 SOLIDWORKS 内部创建时间和完整文件名生成；新增持久 SHA-256 缓存和基于远端注册表的版本判断。
- `class=robot` 改为 assembly_package XML 的 `robot_id` 地址引用；不生成 Asset 包、几何、源模型或注册记录。
- 新增“导出 assembly_package”选项；关闭后只处理非 Robot Asset，并跳过装配包本地输出、Wanxiang assembly_package 上传及 `assembly_version` 检查。
- 分类和 manifest 统一只读取文件级“自定义”属性，忽略“配置特定”属性；缺失或空白的必填字段会一次性汇总提示。
- Wanxiang 发布严格校验可发布 `class` 拼写，并增加逐项上传进度、HTTP 响应诊断和不包含 API key 的本地日志。
- 非 Asset 装配体改为持续拆分到叶节点；Asset 仍是硬边界，不读取其内部节点属性。
- 加强轻量化、虚拟/内嵌组件、模型窗口生命周期和 Pack and Go 隔离处理，避免误打包父装配体或同级分支。
- 分类预览新增源文件快照、变化复核、阶段进度和取消；导出不再重复执行完整分类。
- Asset 只收集原始 SLDDRW，不再生成 PDF；源模型和图纸统一放入 `source/models/`。
- 新增 Asset class、版本、配置属性和文本属性检查脚本，以及补填零件名和统一 Asset 版本的 SOLIDWORKS 宏。
- 安装脚本会为当前桌面用户自动启用 Add-in，并随安装包附带对应启动项脚本。
- 发布流程隔离契约测试桩与正式 Release，并在打包时强制校验真实 SOLIDWORKS Interop，防止生成“DLL 能进入进程但不能建立 `ISwAddin` 回调”的无效安装包。

## 现场验收清单

在 SOLIDWORKS 2025、2026 各执行一遍：

1. 编译、安装、启动、启用 Add-in、卸载。
2. `Root → 非Asset装配 → Asset + 非Asset装配`：父节点为 Group；Asset 停止，非 Asset 装配继续拆到叶节点。
3. 第三级 Asset：所有非 Asset 装配分支都继续展开；命中 Asset 的分支在 Asset 根停止。
4. 整机无 Asset：所有非 Asset 装配体均为 Group，逐层拆到 assembly_package 几何叶节点，每个几何叶节点各有一对 STEP/STL。
5. 顶层为 Asset：XML 只有一个 Asset；通过调试/日志确认分类扫描没有调用其 `GetChildren`。
6. 复用同一 Asset 版本；修改源模型但不提升版本时必须拒绝。
7. assembly_package STEP/STL 均存在；切换格式只改变 XML 的 mesh 引用。
8. 在两个导出模型中检查原点；用 XML 位姿重建装配并与 SOLIDWORKS 比较。
9. 零件 Asset 的 `source/models/` 只包含自身 SLDPRT 及直接关联 SLDDRW；装配体 Asset 只包含根及向下层级内的 SLDASM/SLDPRT/SLDDRW，且能在隔离目录打开；确认父装配体、同级分支和 assembly_package 文件均未混入。
10. 检查 Asset 根模型及层级内每个子装配体、零件直接关联的 SLDDRW 均与源模型位于同一 `source/models/` 目录，并确认没有生成 PDF、无关图纸没有被导出。
11. 设置 `class=robot`、文件级“设计目的”和正整数 `asset_version`，重新预览和导出；确认 Robot 没有生成普通 Asset 包或注册记录，也没有 Robot STEP/STL，assembly_package XML 保留 `kind="robot"`、原层级/位姿，并写 `mesh asset_id="<robot-uuid>:<version>"`。用参考脚本确认名称 UUID 与 XML 一致，消费者能通过 NAS 原始名称目录索引定位版本 URDF；Robot 升版不改变 UUID。其他 class 仍按第 9、10 项打包。
12. 人工制造中途失败，确认 staging 被清理且既有版本目录未被覆盖。
13. 导出前后确认模型活动配置和显示状态未被切换，并确认选择和 STEP/STL 系统选项已恢复。

镜像或带非单位缩放的组件变换不能无损表示成平移加四元数，因此插件会明确拒绝，而不是输出错误位姿。

## SOLIDWORKS API 依据

- [SOLIDWORKS API Programming Guide / Interop](https://help.solidworks.com/2026/English/api/sldworksapiprogguide/Welcome.htm)
- [GetRootComponent3](https://help.solidworks.com/2026/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IConfiguration~GetRootComponent3.html)
- [Component GetChildren](https://help.solidworks.com/2025/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IComponent2~IGetChildren.html)
- [MathTransform 矩阵布局](https://help.solidworks.com/2026/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IMathTransform.html)
- [STEP 导出选项](https://help.solidworks.com/2026/english/api/swconst/FileSaveAsSTEPOptions.htm)
- [STL 导出选项](https://help.solidworks.com/2026/English/api/swconst/FileSaveAsSTLOptions.htm)
- [Pack and Go](https://help.solidworks.com/2026/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IPackAndGo.html)
- [Pack and Go 属性](https://help.solidworks.com/2026/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IPackAndGo_properties.html)
- [Referenced Documents 搜索目录](https://help.solidworks.com/2023/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ISldWorks~GetSearchFolders.html)

### Workstation XML 节点

装配体文件级自定义属性 is_workstation=true（也支持 1、yes，忽略大小写）时，XML node 输出 kind=workstation，同时附带 workstation_name 属性。保留原有位姿、子节点及 mesh 引用；顶层总装标为 workstation 时也保留该节点。配置特定属性不参与判断。修改这些属性后，已有装配包需提升 assembly_version。

## 1.1.0-rc1 实例承载关系

后续 `1.1.0-rc2` 改为父组件分组展示，支持连续点选、同级同款批量添加、成功后自动清空、链式接续及面板内保存。112 项自动回归及 200 子组件实机交互见[交互改进记录](docs/INSTANCE_BINDING_INTERACTION_RESULTS.md)。

新增“承载关系”命令：父单选、子多选、批量校验、解绑、更换父及高亮检查。关系随总装配保存并按配置隔离，使用组件持久引用恢复。assembly_package 新增 `assembly-relations.json`，纳入指纹、文件哈希和上传 ZIP；首次迁移及绑定变化需要提升已有 assembly_version。

见 [操作与协议](docs/INSTANCE_BINDING_PROTOCOL.md)、[验收记录](docs/INSTANCE_BINDING_TEST_RESULTS.md) 和 [真实导出样例](examples/instance-binding/README.md)。当前为候选版本，105 项自动回归与 SW2025 SP5.0 实机验证已完成，逐项覆盖和待联调内容以验收记录为准。
