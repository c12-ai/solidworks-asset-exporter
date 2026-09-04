# SOLIDWORKS Asset / Project 混合导出插件

这是一个面向 SOLIDWORKS 2025/2026、.NET Framework 4.8、64 位进程的 C# COM Add-in。它把当前装配体拆成四种节点：

- `Asset`：当前节点 `is_asset=true|1|yes`。这是硬终止边界，分类扫描不会读取其子节点，也不会在内部发现第二个 Asset。
- `Project`：没有可继续拆分子节点的非 Asset 叶节点，作为一个项目本地 STEP/STL 单元。
- `Group`：自身不是 Asset，并且仍有可见、未抑制、非包络子节点。保留装配层级和位姿，没有 mesh，并继续向下拆分。
- `Robot`：`is_asset=true` 且 `class=robot` 的硬边界。它不生成 Asset 包或几何，只在启用 Project 导出时保留层级、位姿和 `robot_id` 地址。

拆分只在 Asset 处停止。非 Asset 装配体无论后代是否包含 Asset 都会继续向下；最终非 Asset 叶节点作为 `Project` 单元进入当前项目目录，不污染全局 Asset 库。

当前发布版本为 `v1.0.5`。

## 当前实现范围

- 两阶段分类；Asset 硬边界短路，非 Asset 装配层级递归拆到叶节点。
- 分类预览通过 Wanxiang `GET /asset/registry` 读取服务端维护的注册信息，再读取装配树、分类属性、Asset 边界内源模型及直接关联图纸并计算内容 SHA-256 指纹。同目录同名 SLDDRW 只通过文件系统查找，预览不执行 SOLIDWORKS“打开工程图”命令。源模型、图纸和已有本地包校验统一使用持久哈希缓存；第一次读取文件内容，后续仅在完整路径、大小或最后修改时间变化时重新计算。版本判断只以本次读取的远端同 UUID 指纹为准；远端首次没有注册表时由接口返回逻辑空表，默认不把注册表保存在本地。本地已有包不是版本依据：远端版本有效但本地包损坏、旧格式或指纹冲突时不要求升版，确认导出后先把旧目录移至 Asset 根目录的 `.local-package-backups`，再用当前源文件重建同版本。只有装配体 Asset 在确认导出后使用 Pack and Go；零件 Asset 直接复制自身，所有 Project 指纹直接读取自身保存文件。
- 分类扫描会过滤隐藏、真正抑制、包络组件；所有分类与业务属性只从模型文件级“自定义”页读取，完全忽略“配置特定”页。`is_asset`、`asset_version`、`assembly_version` 按准确键名和安全缓存读取，不再误用只能提供配置特定属性的组件管理器，也不会因属性求值把模型标成已修改。普通轻量化模型按需隐藏打开、读取后立即关闭；`swx...\VC~~`/`IC~~` 虚拟或内嵌组件绝不独立打开和关闭，只在父装配中临时解析并恢复原轻化状态。结果按源文件缓存，同一文件的重复实例只读取一次。
- Asset 分类边界保持短路：父节点一旦命中 Asset，会在读取 `GetChildren()` 之前返回，不校验子零件/子装配体属性，也不会生成内部节点。源文件收集仅从组件引用读取路径和文档类型，不再为了打包而打开子文档读取元数据。
- UUIDv5、版本规则、文件级必填属性、已保存/无未保存修改校验。分类预览会在计算 Asset 指纹和远端版本之前识别 Asset 根本身是否为虚拟/内嵌组件；这类根不会被当作普通升版模型打开，会明确要求先保存为外部 SLDASM/SLDPRT，并禁止导出。
- Asset UUID v2 只使用 SOLIDWORKS 内部创建时间和完整文件名（含扩展名）生成稳定 UUIDv5 哈希；配置、显示状态、路径和内容版本不参与 Asset 身份。预览发现内容指纹已变化但 `asset_version` 未提升时，会把对应 Asset 模型以可编辑方式直接打开、激活，并弹出当前版本和建议版本的醒目提示。
- STEP AP214 和二进制 Fine STL 双格式导出；模型文档本地原点作为几何原点。
- XML 父子相对位姿、米、`qx/qy/qz/qw`、Asset ID 或 Project 相对 mesh 路径。
- Asset manifest、SHA-256、内容指纹、版本命中复用、同 ID 内容变化拒绝覆盖。
- 只有装配体 Asset 调用 Pack and Go：零件 Asset 只复制自身 SLDPRT，Project 零件和装配体都不调用 Pack and Go。由于部分 SOLIDWORKS 会拒绝 `SetDocumentSaveToNames` 的过滤清单，装配体 Asset 的完整 Pack and Go 结果先写入当前导出事务内的隔离临时目录，随后只把 Asset 根及向下层级内明确收集的外部 SLDASM/SLDPRT 提升到最终 `source/models/`；父装配体、同级分支和上下文外部引用不会进入 manifest 或上传包，临时结果立即清理。`SavePackAndGo` 返回状态数组长度与原始依赖清单不一致时，不再仅凭数量误判失败，而是要求每个外部 Asset 目标文件实际存在后才提交。名称包含 `子组件^所属装配体` 或位于 SOLIDWORKS `VC~~/IC~~` 会话目录的虚拟组件保留在所属 SLDASM 内，不要求生成不存在的独立源文件；Asset 根本身若为虚拟组件则要求先保存为外部文件。
- `class=robot`（忽略大小写和首尾空格）不作为 Asset 导出：不参与 Asset UUID、指纹、版本、manifest、注册或源文件打包，也不生成 STEP/STL。启用 Project 导出时，Robot 仅在 XML 原层级和位姿处写入 `<mesh robot_id="设计目的:asset_version" />`；其他 `class` 的 Asset 与普通 Project 几何流程不变。
- 外部 Asset 组件已加载时会核对 `GetModelDoc2()` 文档路径与组件 SLDASM/SLDPRT 源路径；如果 SOLIDWORKS 返回了父装配文档，插件改为按 Asset 源路径获取正确文档。Pack and Go 前再次校验 Asset 根路径，防止把整机误打包进 Asset。
- 为上述每个 SLDASM/SLDPRT 收集直接关联的 SLDDRW；只复制原始图纸，不生成 PDF，并与 SLDASM/SLDPRT 一起平铺到 Asset 的 `source/models/` 目录。
- Asset 与 Project 独立 staging，成功后目录级提交；既有版本不覆盖。
- 分类预览会完成装配树分类，同时计算 Asset 与 Project 内容指纹：Asset 立即对照 Wanxiang 注册表判断 `asset_version`，Project 立即检查本地相同 UUID/`assembly_version` 的 `export-report.json`。取消“导出 Project”后，不要求 `assembly_version`，也不计算或校验 Project 指纹；Asset 预览、导出和注册仍照常执行。Robot 不进入 Asset/Project 几何指纹队列；`robot_id` 直接由文件级“设计目的”和正整数 `asset_version` 以英文冒号拼接生成，例如 `Hebe:1`。任何版本冲突都在预览中显示并禁用“导出”，不会等到导出阶段才首次发现。属性缺失、模型读取、Asset 指纹和 Project 指纹等可继续检查的问题会遍历完成后一次性汇总提示，不再每次只显示第一项。预览会保留 Asset/Project 指纹以及源文件/打开文档快照；点击导出时不再执行第二次完整分类或重新计算 Project 指纹，只校验源文件大小与修改时间、会话内更新标记、活动配置、Wanxiang 最新 Asset 注册表及 Project 本地版本状态，变化时要求重新预览。Project 指纹按“唯一源文件+配置”去重，重复装配实例不重复读取同一文件；Project 零件和装配体均直接使用持久 SHA-256 缓存，不调用 Pack and Go。导出窗口显示当前 Asset/Project 阶段并可在单个 SOLIDWORKS 操作结束后取消。
- 导出模型文档当前活动配置及显示状态，不切换配置或显示状态；导出后恢复选择和 STEP/STL 全局设置。
- 几何、Pack and Go 和关联图纸操作统一按需获取模型文档，完成后关闭插件本次打开或临时激活的窗口。装配体引用件不再被强制以只读模式打开；`CloseDoc` 后仍注册在 SOLIDWORKS 时会调用 `QuitDoc`。父装配体可以继续把不可见、非只读的组件文档保留在内存中，这不再被误判为窗口关闭失败；总装以及用户原先已打开的模型窗口保持不变。
- 可选在本地导出成功后直接上传到 [wanxiang-data-service](https://github.com/c12-ai/wanxiang-data-service)：每个 Asset 版本目录打成 ZIP 后，通过 Wanxiang 0.4.0 的 `PUT /asset/{uuid}/v{version}` 单次原子发布，请求头携带 `X-Content-Fingerprint`；Project 仍通过 `PUT /archive/projects/{path}` 上传。插件不通过通用文件接口写 `assets/` 或注册表；请求使用 Bearer API key 并绕过开发机代理。
- 上传顺序固定为 Asset 原子发布、Project 上传。Asset 发布接口的 201 新注册和 200 幂等复用都视为成功；409 冲突不会留下半发布目录或改动注册表。只有启用“保存本地副本”时，发布完成后才通过 `GET /asset/registry` 下载服务端最终注册表并同步到本地。
- 上传状态逐项显示 `Asset N/M`，Project 分别显示打包、发送、收到 HTTP 响应和校验完成。每次上传都会在 `%LOCALAPPDATA%\SolidWorksAssetExporter\uploads` 创建独立日志，记录请求 URL、HTTP 状态码、耗时和截断后的响应摘要（不记录 API key）；插件界面可通过“查看上传日志”直接打开。上传请求使用完整响应读取，使 30 分钟 HTTP 超时同时覆盖请求体和响应体，避免服务端已处理但客户端无限等待响应结束。

现有 72 项自动测试，包括 Asset 硬边界短路、父 Asset 跳过子属性、Asset UUID 仅由内部创建时间和文件名生成、配置特定属性完全忽略、必填 Asset 属性完整汇总、Wanxiang 0.4.0 `class` 拼写校验、Asset 指纹版本复用/升级/重复内容判断、Robot 仅生成 Project XML 地址引用、关闭 Project 后跳过其本地/远端输出、需升版外部 Asset 自动打开且非只读、虚拟 Asset 根不作为升版文档打开、远端未注册的本地冲突包备份重建且不误报升版、预览纯文件系统图纸查找、原始 SLDDRW 与源模型同目录且不生成 PDF、未变化文件哈希缓存复用、零件 Asset 及 Project 零件/装配体均不调用 Pack and Go、预览后源文件变化拒绝导出、Wanxiang `GET /asset/registry` 预览、默认不保存本地副本和逻辑空注册表、`PUT /asset/{uuid}/v{version}` 原子发布及 Asset→Project 顺序、上传逐项进度、可读日志和不泄漏 API key 的 HTTP 响应诊断、轻化组件读取文件级 `is_asset=1`、同一源文件的重复实例只读取一次、已加载组件返回父装配文档时按源路径回退、虚拟/内嵌组件只在父装配中解析并恢复原轻化状态、Pack and Go 允许虚拟子组件不作为独立文件出现、Pack and Go 只在临时激活目标 Asset 子装配体后运行并恢复原窗口、Pack and Go 隔离 Asset 边界外上下文引用、Pack and Go 状态数组长度变化但 Asset 输出完整时继续提交、插件解析或配置恢复产生的 dirty 标志与后续用户模型修改相区分、完整元数据只读取文件级自定义属性、Wanxiang Bearer/URL/ZIP 契约、模型窗口所有权、避免强制只读、`QuitDoc` 关闭回退、父装配内存引用和激活警告；Add-in 代码路径也可使用 `InteropStubs.cs` 做隔离契约构建，生产构建不会包含该 stub。

本项目已在 SOLIDWORKS Premium 2025 SP5.0 和官方 Interop 33.5.0.53 上完成生产构建、COM 安装与加载和命令打开验证。轻量化装配体按需文档生命周期、完整 STEP/STL、装配体 Asset Pack and Go 和 SLDDRW 产物仍需完成最终现场验收。SOLIDWORKS 2026 尚未实机验证。

## 自定义属性

在总装配体根模型设置：

```text
assembly_version = 1       # 必填正整数
```

在 Asset 根零件或子装配体设置：

```text
is_asset = true            # true / 1 / yes
asset_version = 1          # 必填正整数
```

插件只读取模型文件级“自定义”页，不读取“配置特定”页；配置级属性无论是否为空、是否与文件级同名，均不参与分类、版本判断或 manifest。父节点已是 Asset 时，子节点属性完全不参与分类或 manifest 校验。Asset manifest 只保存 Asset 根文件级自定义属性。

### Asset 属性填写说明

以下属性必须填写在模型文件级“自定义”页；配置特定属性会被插件忽略。`asset_attr` 只是 Property Tab Builder 中的界面分组标题，不是需要写入模型的属性。Asset 的 `零件名` 缺失或为空白时，分类预览会在完成全部可执行检查后统一列出并禁止导出。

| 属性 | 类型/格式 | 填写说明 |
| --- | --- | --- |
| `零件名` | 文本 | Asset 必填名称；必须位于文件级“自定义”页且不能为空。 |
| `is_asset` | 布尔值 | 是否把当前零件或子装配体作为 Asset。填写 `true`、`1` 或 `yes`。Asset 是硬边界，分类扫描不会继续读取其内部节点。 |
| `class` | 单选枚举 | Robot 使用 `robot`；发布到 Wanxiang `assets/` 的普通 Asset 只能使用 `movable`、`equipment`、`structure`。旧拼写 `moveable` 会在分类预览中报错。 |
| `is_tool` | 布尔值 | 是否作为机器人使用的工具。Tool 本身也可以属于 `movable`，例如在快换过程中由机器人 attach；Tool 还需要在后续定义 TCP Point。建议统一填写 `true` 或 `false`。 |
| `accepts_robots` | 文本列表 | 与该 Asset 兼容的机器人型号、名称或约定 ID；多个值使用英文分号 `;` 分隔。没有已确认的兼容机器人时留空，留空不表示兼容全部机器人。 |
| `is_fixture` | 布尔值 | 是否具有定位、夹持、承载或接收其他 Asset 的治具功能。`movable` 和 `structure` 都可以同时是 Fixture。 |
| `accepts_interface` | 文本列表 | Fixture 可以接受的物料接口；多个接口使用英文分号 `;` 分隔。接口名称由团队人工约定，相同接口应复用已有名称。 |
| `is_placement_required` | 布尔值 | 当前 Asset 是否必须安装或放置在另一个 Asset 上才能使用。 |
| `placement_interface` | 文本 | 当前 Asset 自己提供的放置接口，接口类型与 Fixture 的 `accepts_interface` 使用同一套人工约定名称，例如 `50ML_tube`。 |
| `slots_num` | 非负整数 | 当前 Asset 可提供的安装槽位、工位或容纳位置数量；`0` 表示不提供槽位。 |
| `is_adjustable` | 布尔值 | Asset 的安装位姿或空间布局位置是否允许调整。`movable` 通常不可调，`structure` 可以根据布局需要设为可调。 |
| `asset_version` | 正整数 | Asset 内容版本，从 `1` 开始。源模型、图纸或关键内容改变时必须提升版本，不能覆盖已经发布的同版本 Asset。`class=robot` 时直接作为 `robot_id` 冒号后的版本号。 |
| `设计原理` | 文本 | 说明该 Asset 实现功能所采用的机械、电气或控制原理。 |
| `设计目的` | 文本 | 说明设计该 Asset 要解决的问题、目标和预期用途；`class=robot` 时直接作为 `robot_id` 第一段，例如 `Hebe`。 |
| `升版说明` | 文本 | 说明当前版本相对上一版本的修改内容和升版原因；初始版本可填写“初始版本”。 |

`class` 的建议含义：

- `movable`：机器人能够通过 Tool 操作的物体，通常设置为 `is_adjustable=false`。后续必须为其定义供 Tool attach 的抓取 Point。Tool 本身也可以属于 `movable`，用于机器人快换。
- `robot`：执行机构。
- `equipment`：离心机等外部设备。后续可以定义多个交互 Point，例如按按钮、开盖或其他操作位置。
- `structure`：不定义交互 Point，也不与机器人直接交互的结构；可以设置 `is_adjustable=true`，表示其空间布局位置允许调整。

以上属性只描述机械工程师在设计阶段能够直接确定的 Asset 分类和治具关系。Position、Area、抓取 Point、TCP Point、设备交互 Point 等空间定义不在当前 Property Tab 中填写，后续直接定义在资产数据中。

条件填写约定：

- `movable` 可以同时设置 `is_tool=true`；此时必须填写 `accepts_robots`，表示该工具已经适配的机器人。
- `movable` 可以同时设置 `is_fixture=true`；此时必须填写 `accepts_interface`，表示该治具可以放置的物料接口，并按需要填写 `slots_num`。
- `movable` 可以设置 `is_placement_required=true`；此时必须填写当前 Asset 自己提供的 `placement_interface`。
- `structure` 也可以设置 `is_fixture=true` 和 `is_adjustable=true`，分别表示它能够接收物料接口、且允许调整空间布局位置。
- 接口匹配采用简单的名称精确匹配：当前 Asset 的 `placement_interface` 必须出现在承载方的 `accepts_interface` 列表中。
- `is_adjustable=true` 只表示安装位姿可调，不表示 Asset 的所有机械或工艺参数均可调。

填写示例：

| Asset | `class` | 关键属性 |
| --- | --- | --- |
| 50 ml 试管 | `movable` | `is_placement_required=true`；`placement_interface=50ML_tube` |
| 50 ml 试管夹 | `movable` | `is_fixture=true`；`accepts_interface=50ML_tube`；`is_placement_required=true`；`placement_interface=50ML_tube_fix` |
| 试管架治具 | `structure` | `is_fixture=true`；`accepts_interface=50ML_tube_fix`；`is_adjustable=true` |

当前插件使用 `is_asset` 进行分类。所有 Asset（包括 Robot）在 `is_asset=true|1|yes` 时都要求正整数 `asset_version`；Robot 地址固定按“设计目的:asset_version”生成。只有勾选“导出 Project”时才在总装配体上强制要求正整数 `assembly_version`。其余字段按上述业务约定填写并保存到非 Robot Asset manifest，暂不参与导出分类或程序校验。

同一 `uuid + asset_version` 的源模型及图纸内容完全一致时，插件会校验现有文件并直接复用旧 Asset；如果内容已经变化，则拒绝用同一个版本号覆盖，必须提升 `asset_version`。Asset 注册信息以 Wanxiang 注册表为准；本地 Asset 地址由输出根目录、UUID 和版本确定，每个版本目录中的 manifest 是该 Asset 的文件与哈希记录。

Asset UUIDv5 v2 的输入严格限定为 SOLIDWORKS 内部创建时间和文件名（含扩展名）。绝对路径、配置、显示状态、文件内容和 `asset_version` 不参与 UUID，因此移动目录、切换配置或正常升版不会改变资产身份；重命名文件或内部创建时间不同会得到新 UUID。同一源文件的多个装配实例保留各自 XML 节点和位姿，但共享同一个 `asset_id`，Asset 包只创建或复用一次。`asset_id` 是 `<uuid>:<version>`，只出现在装配 XML；manifest 内只保存独立的 `uuid` 和 `version`。v1.0.5 切换到 v2 身份前缀，因此旧版本插件生成的 UUID 不会与新规则混用；旧注册项保留但不会被误认为新身份的当前版本。

Asset 的 `content_fingerprint` 分两层计算：先对 Asset 根模型及其边界内所有未抑制的 SLDASM/SLDPRT，按“文件名、文件长度、文件 SHA-256”排序后与根模型身份种子一起计算模型指纹；再把每个模型同目录、同名的直接关联 SLDDRW 按“文件名、文件 SHA-256”排序加入，得到最终内容指纹。因此，只要保存操作改变了上述任一源模型/图纸的字节，或者增删、重命名、抑制/解除抑制这些文件，指纹都会变化。根模型当前配置或显示状态变化也会改变指纹。修改几何、尺寸、材料或自定义属性并保存，通常都会因为 SLD 文件内容改变而要求新版本。

预览不会为了指纹打开每个模型或工程图。文件 SHA-256 缓存在当前用户 `%LOCALAPPDATA%/SolidWorksAssetExporter/asset-file-hashes.json`，缓存键包含完整路径、文件大小和最后修改时间；缓存损坏或无法写入时只会退化为重新计算，不会阻断预览或导出。

绝对目录、Asset 在父装配体中的实例位姿/配合、父装配体边界外的修改、上传地址和注册表时间戳不参与 Asset 内容指纹；生成后的 STEP/STL 也不是源内容指纹的输入，但 manifest 会另外校验所有导出文件的大小和 SHA-256，缺失或被改动仍不能复用。Robot 不生成 Asset 指纹或 manifest。同一 UUID 和 `asset_version` 已存在但内容指纹不同，插件不会自动覆盖或自动改版本，必须手动增加正整数 `asset_version`。

## 输出

“Asset 本地输出根目录”保存可跨 Project 复用的 Asset 版本包；“Project 本地输出根目录”保存当前总装配体的 XML、Project STEP/STL 和导出报告。勾选“导出 Project”时两者必须分开，防止全局 Asset 库与一次性 Project 结果互相覆盖；关闭后 Project 根目录可以留空，只导出/上传非 Robot Asset。

```text
asset-library/
  <asset-uuid>/v<asset-version>/
    asset_<uuid>_v<version>.json
    geometry/model.step
    geometry/model.stl
    source/models/             # SLDASM/SLDPRT/SLDDRW

project-export/
  <assembly-uuid>/v<assembly-version>/
    assembly_<assembly-uuid>_v<version>.xml
    meshes/<project-unit-uuid>/model.step
    meshes/<project-unit-uuid>/model.stl
    export-report.json
```

Project 单元永远同时生成 STEP 和 STL；窗口中的格式选项只决定 XML 的 `mesh file` 引用哪个格式。Project 不生成 SLDPRT/SLDASM、SLDDRW、PDF 或 Asset manifest。
Robot 节点不属于 Project 几何单元，因此不会出现在 `meshes/` 中；开启 Project 时只在 XML 中写 `robot_id`。

## Wanxiang 数据服务与注册表

分类预览默认直接读取 Wanxiang 注册表，因此无论是否勾选“本地导出成功后直接上传”，都需要填写：

- 数据服务地址：默认 `http://192.168.12.87:8100`。
- API key：不写入插件 `settings.json`，单独保存在当前 Windows 用户的 `~/.config/wanxiang/data-service-api-key`。
- 保存本地副本：默认关闭；启用后才把本次读取或上传后的 `asset-registry.json` 同步到 Asset 资产库根目录。

远端目录布局由插件固定，界面和配置文件均不能修改：

```text
assets/
  asset-registry.json
  <asset-uuid>/v<asset-version>/...
projects/
  <assembly-uuid>/v<assembly-version>/...
```

分类预览固定调用 `GET /asset/registry`；注册表尚不存在时服务返回逻辑空表及 `X-Registry-Exists: false`。每个 Asset 版本通过 `PUT /asset/{uuid}/v{version}` 上传完整 ZIP，并在 `X-Content-Fingerprint` 中发送 manifest 指纹；服务端校验 manifest、必需文件、大小和 SHA-256 后一次性提交版本目录与注册表。Project 继续使用 `PUT /archive/projects/...`。网络或服务端失败不会删除本地导出结果；相同包可幂等重试，409 冲突必须重新预览或修改 `asset_version`。

Robot 不调用 `PUT /asset/{uuid}/v{version}`。它只作为 Project XML 中的 `robot_id="设计目的:asset_version"` 地址引用。

XML 示例：

```xml
<assembly schema_version="1.0" uuid="..." version="3"
          project_mesh_format="step" length_unit="m" quaternion_order="xyzw">
  <nodes>
    <node id="..." parent_id="" name="Tooling-1" kind="group">
      <pose tx="0" ty="0" tz="0" qx="0" qy="0" qz="0" qw="1" />
    </node>
    <node id="..." parent_id="..." name="Motor-1" kind="asset">
      <pose tx="0.1" ty="0" tz="0" qx="0" qy="0" qz="0" qw="1" />
      <mesh asset_id="<uuid>:2" />
    </node>
    <node id="..." parent_id="..." name="Fixture-1" kind="project">
      <pose tx="0" ty="0.2" tz="0" qx="0" qy="0" qz="0" qw="1" />
      <mesh file="meshes/<project-unit-uuid>/model.step" />
    </node>
    <node id="..." parent_id="..." name="Robot-1" kind="robot">
      <pose tx="0" ty="0" tz="0" qx="0" qy="0" qz="0" qw="1" />
      <mesh robot_id="Hebe:1" />
    </node>
  </nodes>
</assembly>
```

Asset、Project 都是叶节点；Group 没有 mesh。XML 不输出 joint、轴或关节类型。混合拆分时至少要有一个可见、未抑制、非包络的固定顶层组件，并把固定组件优先写入节点序列，但不会把真实位姿归零。

## 构建与测试

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

## 安装与卸载

### v1.0.5 升版说明

- 同步 Wanxiang 0.4.0：Asset 使用单次 ZIP 原子发布，Project 保持目录 ZIP 上传；发布冲突不会留下半成品或覆盖远端数据。
- Asset UUID 升级为 v2 规则，仅由 SOLIDWORKS 内部创建时间和完整文件名生成；新增持久 SHA-256 缓存和基于远端注册表的版本判断。
- `class=robot` 改为 Project XML 的 `robot_id` 地址引用；不生成 Asset 包、几何、源模型或注册记录。
- 新增“导出 Project”选项；关闭后只处理非 Robot Asset，并跳过 Project 本地包、上传及 `assembly_version` 检查。
- 分类和 manifest 统一只读取文件级“自定义”属性，忽略“配置特定”属性；缺失或空白的必填字段会一次性汇总提示。
- Wanxiang 发布严格校验可发布 `class` 拼写，并增加逐项上传进度、HTTP 响应诊断和不包含 API key 的本地日志。
- 非 Asset 装配体改为持续拆分到叶节点；Asset 仍是硬边界，不读取其内部节点属性。
- 加强轻量化、虚拟/内嵌组件、模型窗口生命周期和 Pack and Go 隔离处理，避免误打包父装配体或同级分支。
- 分类预览新增源文件快照、变化复核、阶段进度和取消；导出不再重复执行完整分类。
- Asset 只收集原始 SLDDRW，不再生成 PDF；源模型和图纸统一放入 `source/models/`。
- 新增 Asset class、版本、配置属性和文本属性检查脚本，以及补填零件名和统一 Asset 版本的 SOLIDWORKS 宏。
- 安装脚本会为当前桌面用户自动启用 Add-in，并随安装包附带对应启动项脚本。
- 发布流程隔离契约测试桩与正式 Release，并在打包时强制校验真实 SOLIDWORKS Interop，防止生成“DLL 能进入进程但不能建立 `ISwAddin` 回调”的无效安装包。

从 GitHub Releases 下载 `SolidWorksAssetExporter-v1.0.5.zip` 并完整解压。关闭 SOLIDWORKS，右键解压目录中的 `Install.cmd`，选择“以管理员身份运行”。如果从源码目录安装，则运行：

```text
.\scripts\install.cmd
```

安装脚本会把启动项写入当前桌面用户（即使安装时使用了另一管理员账户）。重新启动 SOLIDWORKS 后，执行 `Asset / Project 导出` 命令。首次使用先设置 Asset/Project 本地输出根目录、XML mesh 格式、Wanxiang 地址和 API key，然后点击“分类预览”。Wanxiang 远端目录固定为 `assets` 和 `projects`。

### v1.0.5 升级步骤

1. 等待当前预览或导出结束，然后完全关闭 SOLIDWORKS。
2. 解压 `SolidWorksAssetExporter-v1.0.5.zip`，不要直接在 ZIP 内运行脚本。
3. 右键 `Install.cmd`，选择“以管理员身份运行”。脚本会覆盖插件 DLL、重新注册 64 位 COM 并为当前桌面用户启用 Add-in。
4. 启动 SOLIDWORKS，打开已保存的总装配体，先点击“分类预览”，确认 Asset 版本判断后再点击“导出”。
5. 导出时观察窗口底部当前阶段。如需停止，点击“取消导出”；取消会在当前单个 SOLIDWORKS SaveAs/Pack and Go 操作返回后生效。

### 插件未出现在“工具”菜单

1. 确认 SOLIDWORKS 已完全退出，再右键 `Install.cmd` 选择“以管理员身份运行”；安装过程中不要让 `SLDWORKS.exe` 留在后台。
2. 重新启动后在“工具 → 加载项”确认 `Asset / Project 混合导出` 已勾选“活动加载项”和“启动”。
3. 正式 DLL 默认安装在 `%ProgramData%\SolidWorksAssetExporter\SolidWorksAssetExporter.AddIn.dll`，连接日志位于 `%LOCALAPPDATA%\SolidWorksAssetExporter\addin.log`。成功加载会新增 `ConnectToSW succeeded.`。
4. 如果日志没有新增连接记录，不要反复安装同一个未知来源的 ZIP。发布者应重新执行上述四条发布命令；`new-release-package.ps1` 必须成功完成真实 Interop 校验后才能分发 ZIP。

发布 ZIP 根目录同时包含本 `README.md`、`RELEASING.md`、`Install.cmd`、`Uninstall.cmd`、启动项脚本和 `payload/`，完整解压后再安装，不要直接在 ZIP 内运行脚本。

卸载前关闭 SOLIDWORKS，右键发布包中的 `Uninstall.cmd` 并选择“以管理员身份运行”。如果从源码目录卸载，则运行：

```text
.\scripts\uninstall.cmd
```

默认安装位置是 `%ProgramData%\SolidWorksAssetExporter`。设置保存在当前用户 `%APPDATA%\SolidWorksAssetExporter\settings.json`，卸载脚本不会删除用户设置或任何导出数据。

## 现场验收清单

在 SOLIDWORKS 2025、2026 各执行一遍：

1. 编译、安装、启动、启用 Add-in、卸载。
2. `Root → 非Asset装配 → Asset + 非Asset装配`：父节点为 Group；Asset 停止，非 Asset 装配继续拆到叶节点。
3. 第三级 Asset：所有非 Asset 装配分支都继续展开；命中 Asset 的分支在 Asset 根停止。
4. 整机无 Asset：所有非 Asset 装配体均为 Group，逐层拆到 Project 叶节点，每个 Project 叶节点各有一对 STEP/STL。
5. 顶层为 Asset：XML 只有一个 Asset；通过调试/日志确认分类扫描没有调用其 `GetChildren`。
6. 复用同一 Asset 版本；修改源模型但不提升版本时必须拒绝。
7. Project STEP/STL 均存在；切换格式只改变 XML 的 mesh 引用。
8. 在两个导出模型中检查原点；用 XML 位姿重建装配并与 SOLIDWORKS 比较。
9. 零件 Asset 的 `source/models/` 只包含自身 SLDPRT 及直接关联 SLDDRW；装配体 Asset 只包含根及向下层级内的 SLDASM/SLDPRT/SLDDRW，且能在隔离目录打开；确认父装配体、同级分支和 Project 文件均未混入。
10. 检查 Asset 根模型及层级内每个子装配体、零件直接关联的 SLDDRW 均与源模型位于同一 `source/models/` 目录，并确认没有生成 PDF、无关图纸没有被导出。
11. 设置 `class=robot`、文件级“设计目的”和正整数 `asset_version`，重新预览和导出；确认没有生成或注册任何 Robot Asset，也没有 Robot STEP/STL，Project XML 保留原层级/位姿并只写 `mesh robot_id="设计目的:asset_version"`，例如 `Hebe:1`。其他 class 仍按第 9、10 项打包。
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
