# SOLIDWORKS Asset / Project 混合导出插件

这是一个面向 SOLIDWORKS 2025/2026、.NET Framework 4.8、64 位进程的 C# COM Add-in。它把当前装配体拆成三种节点：

- `Asset`：当前节点 `is_asset=true|1|yes`。这是硬终止边界，分类扫描不会读取其子节点，也不会在内部发现第二个 Asset。
- `Project`：没有可继续拆分子节点的非 Asset 叶节点，作为一个项目本地 STEP/STL 单元。
- `Group`：自身不是 Asset，并且仍有可见、未抑制、非包络子节点。保留装配层级和位姿，没有 mesh，并继续向下拆分。

拆分只在 Asset 处停止。非 Asset 装配体无论后代是否包含 Asset 都会继续向下；最终非 Asset 叶节点作为 `Project` 单元进入当前项目目录，不污染全局 Asset 库。

当前发布版本为 `v1.0.4`。

## 当前实现范围

- 两阶段分类；Asset 硬边界短路，非 Asset 装配层级递归拆到叶节点。
- 分类预览通过 Wanxiang `GET /asset/registry` 读取服务端维护的注册信息，再读取装配树、分类属性、Asset 边界内源模型及直接关联图纸并计算内容 SHA-256 指纹。同目录同名 SLDDRW 只通过文件系统查找，预览不执行 SOLIDWORKS“打开工程图”命令。源模型、图纸和已有本地包校验统一使用持久哈希缓存；第一次读取文件内容，后续仅在完整路径、大小或最后修改时间变化时重新计算。版本判断只以本次读取的远端同 UUID 指纹为准；远端首次没有注册表时由接口返回逻辑空表，默认不把注册表保存在本地。本地已有包不是版本依据：远端版本有效但本地包损坏、旧格式或指纹冲突时不要求升版，确认导出后先把旧目录移至 Asset 根目录的 `.local-package-backups`，再用当前源文件重建同版本。只有装配体 Asset 在确认导出后使用 Pack and Go；零件 Asset 直接复制自身，所有 Project 指纹直接读取自身保存文件。
- 分类扫描会过滤隐藏、真正抑制、包络组件；文件级 `is_asset`、`asset_version`、`assembly_version` 从模型文档的文件级自定义属性管理器按准确键名和安全缓存读取，不再误用只能提供配置特定属性的组件管理器，也不会因属性求值把模型标成已修改。普通轻量化模型按需隐藏打开、读取后立即关闭；`swx...\VC~~`/`IC~~` 虚拟或内嵌组件绝不独立打开和关闭，只在父装配中临时解析并恢复原轻化状态。结果按源文件和配置缓存，同一文件的重复实例只读取一次。
- Asset 分类边界保持短路：父节点一旦命中 Asset，会在读取 `GetChildren()` 之前返回，不校验子零件/子装配体属性，也不会生成内部节点。源文件收集仅从组件引用读取路径和文档类型，不再为了打包而打开子文档读取元数据。
- UUIDv5、版本规则、同名属性冲突、已保存/无未保存修改校验。分类预览会在计算 Asset 指纹和远端版本之前识别 Asset 根本身是否为虚拟/内嵌组件；这类根不会被当作普通升版模型打开，会明确要求先保存为外部 SLDASM/SLDPRT，并禁止导出。
- Asset UUID v2 只使用 SOLIDWORKS 内部创建时间和完整文件名（含扩展名）生成稳定 UUIDv5 哈希；配置、显示状态、路径和内容版本不参与 Asset 身份。预览发现内容指纹已变化但 `asset_version` 未提升时，会把对应 Asset 模型以可编辑方式直接打开、激活，并弹出当前版本和建议版本的醒目提示。
- STEP AP214 和二进制 Fine STL 双格式导出；模型文档本地原点作为几何原点。
- XML 父子相对位姿、米、`qx/qy/qz/qw`、Asset ID 或 Project 相对 mesh 路径。
- Asset manifest、SHA-256、内容指纹、版本命中复用、同 ID 内容变化拒绝覆盖。
- 只有装配体 Asset 调用 Pack and Go：零件 Asset 只复制自身 SLDPRT，Project 零件和装配体都不调用 Pack and Go。由于部分 SOLIDWORKS 会拒绝 `SetDocumentSaveToNames` 的过滤清单，装配体 Asset 的完整 Pack and Go 结果先写入当前导出事务内的隔离临时目录，随后只把 Asset 根及向下层级内明确收集的外部 SLDASM/SLDPRT 提升到最终 `source/models/`；父装配体、同级分支和上下文外部引用不会进入 manifest 或上传包，临时结果立即清理。`SavePackAndGo` 返回状态数组长度与原始依赖清单不一致时，不再仅凭数量误判失败，而是要求每个外部 Asset 目标文件实际存在后才提交。名称包含 `子组件^所属装配体` 或位于 SOLIDWORKS `VC~~/IC~~` 会话目录的虚拟组件保留在所属 SLDASM 内，不要求生成不存在的独立源文件；Asset 根本身若为虚拟组件则要求先保存为外部文件。
- `class=robot`（忽略大小写和首尾空格）的 Asset 是纯元数据包：不执行 STEP/STL 导出、不执行 Pack and Go，也不复制 SLDASM、SLDPRT、SLDDRW；版本目录中只有 manifest，且 manifest 的 `files` 为 `[]`。Robot 的内容指纹仍读取实际源模型和关联图纸，因此源设计变化仍会在预览时触发版本判断。其他 `class` 的几何与源文件打包规则不变。
- 外部 Asset 组件已加载时会核对 `GetModelDoc2()` 文档路径与组件 SLDASM/SLDPRT 源路径；如果 SOLIDWORKS 返回了父装配文档，插件改为按 Asset 源路径获取正确文档。Pack and Go 前再次校验 Asset 根路径，防止把整机误打包进 Asset。
- 为上述每个 SLDASM/SLDPRT 收集直接关联的 SLDDRW；只复制原始图纸，不生成 PDF，并与 SLDASM/SLDPRT 一起平铺到 Asset 的 `source/models/` 目录。
- Asset 与 Project 独立 staging，成功后目录级提交；既有版本不覆盖。
- 分类预览会保留已完成的装配树、Asset 指纹和源文件/打开文档快照。点击导出时不再执行第二次完整分类，只校验源文件大小与修改时间、会话内更新标记、活动配置及 Wanxiang 最新注册表；变化时要求重新预览。Project 指纹按“唯一源文件+配置”去重，重复装配实例不再重复打开同一文件；Project 零件直接使用持久 SHA-256 缓存，不再为每个零件调用 Pack and Go。导出窗口显示当前 Asset/Project 阶段并可在单个 SOLIDWORKS 操作结束后取消。
- 导出模型文档当前活动配置及显示状态，不切换配置或显示状态；导出后恢复选择和 STEP/STL 全局设置。
- 几何、Pack and Go 和关联图纸操作统一按需获取模型文档，完成后关闭插件本次打开或临时激活的窗口。装配体引用件不再被强制以只读模式打开；`CloseDoc` 后仍注册在 SOLIDWORKS 时会调用 `QuitDoc`。父装配体可以继续把不可见、非只读的组件文档保留在内存中，这不再被误判为窗口关闭失败；总装以及用户原先已打开的模型窗口保持不变。
- 可选在本地导出成功后直接上传到 [wanxiang-data-service](https://github.com/c12-ai/wanxiang-data-service)：Asset/Project 目录使用 `PUT /archive/{path}`；每个 Asset 版本上传后使用 `POST /asset/registry` 由服务端校验 manifest 并原子注册。插件不再通过通用文件接口写注册表；请求使用 Bearer API key 并绕过开发机代理。
- 上传顺序固定为 Asset、Project、逐项原子注册。注册接口的 201 新增和 200 幂等复用都视为成功；409 冲突不会覆盖远端内容。只有启用“保存本地副本”时，注册完成后才通过 `GET /asset/registry` 下载服务端最终注册表并同步到本地。

现有 64 项自动测试，包括 Asset 硬边界短路、父 Asset 跳过子属性、Asset UUID 仅由内部创建时间和文件名生成、Asset 指纹版本复用/升级/重复内容判断、Robot Asset 只有 manifest 且 `files=[]`、需升版外部 Asset 自动打开且非只读、虚拟 Asset 根不作为升版文档打开、远端未注册的本地冲突包备份重建且不误报升版、预览纯文件系统图纸查找、原始 SLDDRW 与源模型同目录且不生成 PDF、未变化文件哈希缓存复用、零件 Asset 及 Project 零件/装配体均不调用 Pack and Go、预览后源文件变化拒绝导出、Wanxiang `GET /asset/registry` 预览、默认不保存本地副本和逻辑空注册表、`POST /asset/registry` 原子注册及 Asset→Project→注册顺序、轻化组件读取文件级 `is_asset=1`、同一源文件的重复实例只读取一次、已加载组件返回父装配文档时按源路径回退、虚拟/内嵌组件只在父装配中解析并恢复原轻化状态、Pack and Go 允许虚拟子组件不作为独立文件出现、Pack and Go 只在临时激活目标 Asset 子装配体后运行并恢复原窗口、Pack and Go 隔离 Asset 边界外上下文引用、Pack and Go 状态数组长度变化但 Asset 输出完整时继续提交、插件解析或配置恢复产生的 dirty 标志与后续用户模型修改相区分、完整元数据使用模型文档安全缓存且不误读组件作用域、Wanxiang Bearer/URL/ZIP 契约、模型窗口所有权、避免强制只读、`QuitDoc` 关闭回退、父装配内存引用和激活警告；Add-in 代码路径也可使用 `InteropStubs.cs` 做隔离契约构建，生产构建不会包含该 stub。

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

属性可以位于文件级或模型文档当前活动配置级。分类属性 `is_asset`、`asset_version`、`assembly_version` 同名出现在两处时会失败。Asset 根为了写入 manifest 会合并自身全部属性，因此 Asset 根自身的其他同名属性也会失败；父节点已是 Asset 时，子节点属性完全不参与分类或 manifest 校验。Asset manifest 会把 Asset 根合并后的自定义属性保存为 JSON 键值对。

### Asset 属性填写说明

为避免文件级和配置级属性冲突，现场建模时统一建议把以下属性填写在模型的文件级“自定义”页，不要再在配置级建立同名属性。`asset_attr` 只是 Property Tab Builder 中的界面分组标题，不是需要写入模型的属性。

| 属性 | 类型/格式 | 填写说明 |
| --- | --- | --- |
| `is_asset` | 布尔值 | 是否把当前零件或子装配体作为 Asset。填写 `true`、`1` 或 `yes`。Asset 是硬边界，分类扫描不会继续读取其内部节点。 |
| `class` | 单选枚举 | Asset 的主要类别，必须从 `moveable`、`robot`、`equipment`、`structure` 中选择一个。 |
| `is_tool` | 布尔值 | 是否作为机器人使用的工具。Tool 本身也可以属于 `moveable`，例如在快换过程中由机器人 attach；Tool 还需要在后续定义 TCP Point。建议统一填写 `true` 或 `false`。 |
| `accepts_robots` | 文本列表 | 与该 Asset 兼容的机器人型号、名称或约定 ID；多个值使用英文分号 `;` 分隔。没有已确认的兼容机器人时留空，留空不表示兼容全部机器人。 |
| `is_fixture` | 布尔值 | 是否具有定位、夹持、承载或接收其他 Asset 的治具功能。`moveable` 和 `structure` 都可以同时是 Fixture。 |
| `accepts_interface` | 文本列表 | Fixture 可以接受的物料接口；多个接口使用英文分号 `;` 分隔。接口名称由团队人工约定，相同接口应复用已有名称。 |
| `is_placement_required` | 布尔值 | 当前 Asset 是否必须安装或放置在另一个 Asset 上才能使用。 |
| `placement_interface` | 文本 | 当前 Asset 自己提供的放置接口，接口类型与 Fixture 的 `accepts_interface` 使用同一套人工约定名称，例如 `50ML_tube`。 |
| `slots_num` | 非负整数 | 当前 Asset 可提供的安装槽位、工位或容纳位置数量；`0` 表示不提供槽位。 |
| `is_adjustable` | 布尔值 | Asset 的安装位姿或空间布局位置是否允许调整。`moveable` 通常不可调，`structure` 可以根据布局需要设为可调。 |
| `asset_version` | 正整数 | Asset 内容版本，从 `1` 开始。源模型、图纸或关键内容改变时必须提升版本，不能覆盖已经发布的同版本 Asset。 |
| `设计原理` | 文本 | 说明该 Asset 实现功能所采用的机械、电气或控制原理。 |
| `设计目的` | 文本 | 说明设计该 Asset 要解决的问题、目标和预期用途。 |
| `升版说明` | 文本 | 说明当前版本相对上一版本的修改内容和升版原因；初始版本可填写“初始版本”。 |

`class` 的建议含义：

- `moveable`：机器人能够通过 Tool 操作的物体，通常设置为 `is_adjustable=false`。后续必须为其定义供 Tool attach 的抓取 Point。Tool 本身也可以属于 `moveable`，用于机器人快换。
- `robot`：执行机构。
- `equipment`：离心机等外部设备。后续可以定义多个交互 Point，例如按按钮、开盖或其他操作位置。
- `structure`：不定义交互 Point，也不与机器人直接交互的结构；可以设置 `is_adjustable=true`，表示其空间布局位置允许调整。

以上属性只描述机械工程师在设计阶段能够直接确定的 Asset 分类和治具关系。Position、Area、抓取 Point、TCP Point、设备交互 Point 等空间定义不在当前 Property Tab 中填写，后续直接定义在资产数据中。

条件填写约定：

- `moveable` 可以同时设置 `is_tool=true`；此时必须填写 `accepts_robots`，表示该工具已经适配的机器人。
- `moveable` 可以同时设置 `is_fixture=true`；此时必须填写 `accepts_interface`，表示该治具可以放置的物料接口，并按需要填写 `slots_num`。
- `moveable` 可以设置 `is_placement_required=true`；此时必须填写当前 Asset 自己提供的 `placement_interface`。
- `structure` 也可以设置 `is_fixture=true` 和 `is_adjustable=true`，分别表示它能够接收物料接口、且允许调整空间布局位置。
- 接口匹配采用简单的名称精确匹配：当前 Asset 的 `placement_interface` 必须出现在承载方的 `accepts_interface` 列表中。
- `is_adjustable=true` 只表示安装位姿可调，不表示 Asset 的所有机械或工艺参数均可调。

填写示例：

| Asset | `class` | 关键属性 |
| --- | --- | --- |
| 50 ml 试管 | `moveable` | `is_placement_required=true`；`placement_interface=50ML_tube` |
| 50 ml 试管夹 | `moveable` | `is_fixture=true`；`accepts_interface=50ML_tube`；`is_placement_required=true`；`placement_interface=50ML_tube_fix` |
| 试管架治具 | `structure` | `is_fixture=true`；`accepts_interface=50ML_tube_fix`；`is_adjustable=true` |

当前插件使用 `is_asset` 进行分类；当 `is_asset=true|1|yes` 时强制要求正整数 `asset_version`，并在总装配体上强制要求正整数 `assembly_version`。其余字段按上述业务约定填写并保存到 Asset manifest，暂不参与导出分类或程序校验。

同一 `uuid + asset_version` 的源模型及图纸内容完全一致时，插件会校验现有文件并直接复用旧 Asset；如果内容已经变化，则拒绝用同一个版本号覆盖，必须提升 `asset_version`。Asset 注册信息以 Wanxiang 注册表为准；本地 Asset 地址由输出根目录、UUID 和版本确定，每个版本目录中的 manifest 是该 Asset 的文件与哈希记录。

Asset UUIDv5 v2 的输入严格限定为 SOLIDWORKS 内部创建时间和文件名（含扩展名）。绝对路径、配置、显示状态、文件内容和 `asset_version` 不参与 UUID，因此移动目录、切换配置或正常升版不会改变资产身份；重命名文件或内部创建时间不同会得到新 UUID。同一源文件的多个装配实例保留各自 XML 节点和位姿，但共享同一个 `asset_id`，Asset 包只创建或复用一次。`asset_id` 是 `<uuid>:<version>`，只出现在装配 XML；manifest 内只保存独立的 `uuid` 和 `version`。v1.0.4 切换到 v2 身份前缀，因此旧版本插件生成的 UUID 不会与新规则混用；旧注册项保留但不会被误认为新身份的当前版本。

Asset 的 `content_fingerprint` 分两层计算：先对 Asset 根模型及其边界内所有未抑制的 SLDASM/SLDPRT，按“文件名、文件长度、文件 SHA-256”排序后与根模型身份种子一起计算模型指纹；再把每个模型同目录、同名的直接关联 SLDDRW 按“文件名、文件 SHA-256”排序加入，得到最终内容指纹。因此，只要保存操作改变了上述任一源模型/图纸的字节，或者增删、重命名、抑制/解除抑制这些文件，指纹都会变化。根模型当前配置或显示状态变化也会改变指纹。修改几何、尺寸、材料或自定义属性并保存，通常都会因为 SLD 文件内容改变而要求新版本。

预览不会为了指纹打开每个模型或工程图。文件 SHA-256 缓存在当前用户 `%LOCALAPPDATA%/SolidWorksAssetExporter/asset-file-hashes.json`，缓存键包含完整路径、文件大小和最后修改时间；缓存损坏或无法写入时只会退化为重新计算，不会阻断预览或导出。

绝对目录、Asset 在父装配体中的实例位姿/配合、父装配体边界外的修改、上传地址和注册表时间戳不参与 Asset 内容指纹；非 Robot 生成后的 STEP/STL 也不是源内容指纹的输入，但 manifest 会另外校验所有导出文件的大小和 SHA-256，缺失或被改动仍不能复用。Robot manifest 没有文件清单，但仍使用源设计内容指纹判断版本。同一 UUID 和 `asset_version` 已存在但内容指纹不同，插件不会自动覆盖或自动改版本，必须手动增加正整数 `asset_version`。

## 输出

“Asset 本地输出根目录”保存可跨 Project 复用的 Asset 版本包；“Project 本地输出根目录”保存当前总装配体的 XML、Project STEP/STL 和导出报告。两者必须分开，防止全局 Asset 库与一次性 Project 结果互相覆盖。即使启用 Wanxiang 上传，插件也会先在这两个目录完成可校验、可恢复的本地事务导出，成功后再上传，因此目前仍需要同时设置两个根目录。

```text
asset-library/
  <asset-uuid>/v<asset-version>/
    asset_<uuid>_v<version>.json
    # 非 Robot 还包含：
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

分类预览固定调用 `GET /asset/registry`；注册表尚不存在时服务返回逻辑空表及 `X-Registry-Exists: false`。每次上传先通过 `PUT /archive/assets/...` 和 `PUT /archive/projects/...` 上传本次 Asset、Project 目录，再为每个 Asset 调用 `POST /asset/registry`。服务端在互斥区内重读注册表、校验 manifest、执行版本策略并原子替换注册表，因此插件不会直接上传或覆盖 `assets/asset-registry.json`。网络或服务端失败不会删除本地导出结果，注册请求可幂等重试；409 冲突必须重新预览或修改 `asset_version`。

注意：Exporter v1.0.4 的 Robot 是 `files=[]` 的纯元数据 Asset。当前 Wanxiang `main@cf3f075e72b879f9701381795296a2a66eaae695` 仍无条件要求 STEP、STL 和 `source/models/*`，因此 Wanxiang 部署端必须先实现 `class=robot` 例外；否则本地 Robot 包可以正确生成，但 `POST /asset/registry` 会返回 409。

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
  </nodes>
</assembly>
```

Asset、Project 都是叶节点；Group 没有 mesh。XML 不输出 joint、轴或关节类型。混合拆分时至少要有一个可见、未抑制、非包络的固定顶层组件，并把固定组件优先写入节点序列，但不会把真实位姿归零。

## 构建与测试

核心测试不需要安装 SOLIDWORKS：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-core.ps1
```

在没有 SOLIDWORKS 的构建机上检查 Add-in C# 代码路径：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-addin-contract.ps1
```

生产构建默认使用仓库 `third_party\solidworks` 中的三个官方 Interop DLL，因此构建机无需安装 SOLIDWORKS：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-addin.ps1 -Configuration Release
```

`third_party\solidworks` 必须包含：

- `SolidWorks.Interop.sldworks.dll`
- `SolidWorks.Interop.swconst.dll`
- `SolidWorks.Interop.swpublished.dll`

## 安装与卸载

### v1.0.4 升版说明

- 新增 Wanxiang 资产注册表读取、Asset/Project 上传和 Asset 原子注册；注册冲突不会覆盖远端数据。
- Asset UUID 升级为 v2 规则，仅由 SOLIDWORKS 内部创建时间和完整文件名生成；新增持久 SHA-256 缓存和基于远端注册表的版本判断。
- 新增 `class=robot` 纯元数据 Asset，版本目录只生成 manifest，`files=[]`，不导出几何、源模型或图纸。
- 非 Asset 装配体改为持续拆分到叶节点；Asset 仍是硬边界，不读取其内部节点属性。
- 加强轻量化、虚拟/内嵌组件、模型窗口生命周期和 Pack and Go 隔离处理，避免误打包父装配体或同级分支。
- 分类预览新增源文件快照、变化复核、阶段进度和取消；导出不再重复执行完整分类。
- Asset 只收集原始 SLDDRW，不再生成 PDF；源模型和图纸统一放入 `source/models/`。
- 安装脚本会为当前桌面用户自动启用 Add-in，并随安装包附带对应启动项脚本。

从 GitHub Releases 下载 `SolidWorksAssetExporter-v1.0.4.zip` 并完整解压。关闭 SOLIDWORKS，右键解压目录中的 `Install.cmd`，选择“以管理员身份运行”。如果从源码目录安装，则运行：

```text
.\scripts\install.cmd
```

安装脚本会把启动项写入当前桌面用户（即使安装时使用了另一管理员账户）。重新启动 SOLIDWORKS 后，执行 `Asset / Project 导出` 命令。首次使用先设置 Asset/Project 本地输出根目录、XML mesh 格式、Wanxiang 地址和 API key，然后点击“分类预览”。Wanxiang 远端目录固定为 `assets` 和 `projects`。

### v1.0.4 升级步骤

1. 等待当前预览或导出结束，然后完全关闭 SOLIDWORKS。
2. 解压 `SolidWorksAssetExporter-v1.0.4.zip`，不要直接在 ZIP 内运行脚本。
3. 右键 `Install.cmd`，选择“以管理员身份运行”。脚本会覆盖插件 DLL、重新注册 64 位 COM 并为当前桌面用户启用 Add-in。
4. 启动 SOLIDWORKS，打开已保存的总装配体，先点击“分类预览”，确认 Asset 版本判断后再点击“导出”。
5. 导出时观察窗口底部当前阶段。如需停止，点击“取消导出”；取消会在当前单个 SOLIDWORKS SaveAs/Pack and Go 操作返回后生效。

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
11. 设置 `class=robot` 后重新预览和导出，确认 Asset 版本目录只有 manifest、`files=[]`，完全没有 STEP、STL、SLDASM、SLDPRT、SLDDRW；其他 class 仍按第 9、10 项打包。
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
