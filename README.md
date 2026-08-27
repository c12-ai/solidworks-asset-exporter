# SOLIDWORKS Asset / Project 混合导出插件

这是一个面向 SOLIDWORKS 2025/2026、.NET Framework 4.8、64 位进程的 C# COM Add-in。它把当前装配体拆成三种节点：

- `Asset`：当前节点 `is_asset=true|1|yes`。这是硬终止边界，分类扫描不会读取其子节点，也不会在内部发现第二个 Asset。
- `Project`：当前节点的整棵可见子树都不含 Asset。整棵子树作为一个项目本地 STEP/STL 单元。
- `Group`：自身不是 Asset，但可见后代包含 Asset。只保留装配层级和位姿，没有 mesh。

这解决了“一次性定制件不是 Asset、但后续装配重建仍需要数模”的问题：它会作为最大无 Asset 子树的 `Project` 单元进入当前项目目录，不污染全局 Asset 库。

当前发布版本为 `v1.0.3`。

## 当前实现范围

- 两阶段分类和最大无 Asset 子树折叠。
- 分类扫描会过滤隐藏、真正抑制、包络组件；轻化组件不会被误判为隐藏或抑制，分类前会自动完全解析，解析失败时明确报错而不会折叠成 Project。
- Asset 分类边界保持短路；源文件收集只从该 Asset 根节点向下遍历活动层级，不读取子节点的 Asset 语义，也不会生成子节点。
- UUIDv5、版本规则、同名属性冲突、已保存/无未保存修改校验。
- STEP AP214 和二进制 Fine STL 双格式导出；模型文档本地原点作为几何原点。
- XML 父子相对位姿、米、`qx/qy/qz/qw`、Asset ID 或 Project 相对 mesh 路径。
- Asset manifest、SHA-256、内容指纹、版本命中复用、同 ID 内容变化拒绝覆盖。
- Asset 库根目录下的 `asset-registry.json` 注册表；分类预览显示 Asset 是“已注册，预计复用”还是“未注册，预计新建”，正式导出前仍会重新校验模型、图纸、哈希和版本。
- 零件 Asset 只打包自身 SLDPRT；装配体 Asset 只打包根 SLDASM 及其层级内未抑制的子装配体/零件，不包含父装配体、同级分支或层级外依赖。
- 通过 SOLIDWORKS 的“打开工程图”查找上述每个 SLDASM/SLDPRT 同目录、同文件名的直接关联 SLDDRW，并导出对应源图纸和全页 PDF；找不到时按无图纸处理，不扫描其他目录。
- Asset 与 Project 独立 staging，成功后目录级提交；既有版本不覆盖。
- 分类预览和确认窗口；导出模型文档当前活动配置及显示状态，不切换配置或显示状态；导出后恢复选择和 STEP/STL 全局设置。
- SOLIDWORKS Property Tab Builder 零件/装配体/工程图模板，以及自定义属性预览、同步脚本和 VBA 宏。

现有 22 项自动测试，包括 Asset 注册表持久化，以及真实 `SwCadNode` 适配层的可见、轻化和抑制状态测试；Add-in 代码路径也可使用 `InteropStubs.cs` 做隔离契约构建，生产构建不会包含该 stub。

本项目已在 SOLIDWORKS Premium 2025 SP5.0 和官方 Interop 33.5.0.53 上完成生产构建、COM 安装与加载、命令打开、完全解析装配体分类预览，以及当前活动配置不切换的导出流程验证。最新的强类型 `SaveAs3` 修复已完成构建和安装；完整 STEP/STL、Pack and Go、SLDDRW/PDF 产物仍需完成最终现场验收。SOLIDWORKS 2026 尚未实机验证。

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

属性可以位于文件级或模型文档当前活动配置级，但同一个名称不能同时出现在两处，即使值相同也会失败。Asset manifest 会把合并后的自定义属性保存为 JSON 键值对。

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
| `has_QRcode` | 布尔值 | 当前 Asset 是否需要二维码标识。Property Tab Builder 模板使用 `1`/`0`；选中后显示二维码数量、尺寸和间距字段。 |
| `QR_num` | 非负整数 | 当前 Asset 上的二维码数量。没有二维码时填写 `0`；启用 `has_QRcode` 时应填写大于 `0` 的数量。 |
| `QR_size` | 文本 | 单个二维码的实体尺寸，通常指正方形边长。当前模板没有规定单位或格式，项目内必须统一约定并保持一致。 |
| `QR_spacing` | 非负数 | 多个二维码之间的间距。当前模板没有规定长度单位，也没有规定按边缘还是中心测量，项目内必须统一约定；只有一个二维码时填写 `0`。 |
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

二维码字段目前只作为 Asset 自定义属性写入 manifest。插件不会生成二维码内容、二维码图片或打印文件，也不会校验 `QR_num`、`QR_size`、`QR_spacing` 之间的业务关系。填写时建议遵循以下约定：

- `has_QRcode=0`：`QR_num=0`、`QR_spacing=0`，`QR_size` 留空。
- `has_QRcode=1`：`QR_num` 填写大于 `0` 的数量，并填写 `QR_size`；数量大于 `1` 时再按项目统一口径填写 `QR_spacing`。
- 尺寸和间距的单位、测量基准必须由项目统一约定；当前模板和导出程序不会自动换算。

> **零件模板兼容性说明：** `templates/custom-properties/part.prtprp` 中可见标签是 `QR_num`，但当前 `v1.0.3` 模板实际写入的自定义属性名是 `号数30`；`templates/custom-properties/asem.asmprp` 才写入 `QR_num`。因此零件 Asset 的 manifest 会保留 `号数30`，不会自动重命名为 `QR_num`。

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

同一 `uuid + asset_version` 的源模型及图纸内容完全一致时，插件会校验现有文件并直接复用旧 Asset；如果内容已经变化，则拒绝用同一个版本号覆盖，必须提升 `asset_version`。当前不维护独立数据库，Asset 地址由资产库根目录、UUID 和版本确定，每个版本目录中的 manifest 是该 Asset 的文件与哈希记录。

`v1.0.3` 会在 Asset 库根目录维护本地 JSON 注册表 `asset-registry.json`。注册表保存 `asset_id`、UUID、版本、相对目录、内容指纹和注册时间；首次遇到只有旧 manifest、尚未进入注册表的 Asset 时，会校验并自动导入。分类预览只依据注册表显示“预计复用/预计新建”，正式导出仍以文件和内容指纹校验结果为准。该文件是本地索引，不是独立数据库，也不能替代每个版本目录中的 manifest。

Asset UUIDv5 的输入是文件名（含扩展名）、SOLIDWORKS 内部创建时间、模型文档当前活动配置、当前显示状态和文档类型。路径、`asset_version` 不参与 UUID，因此移动模型目录不会改变 UUID。同一零件的多个装配实例保留各自 XML 节点和位姿，但共享同一个 `asset_id`，Asset 包只创建或复用一次。`asset_id` 是 `<uuid>:<version>`，只出现在装配 XML；manifest 内只保存独立的 `uuid` 和 `version`。

### Property Tab Builder 模板与属性同步工具

仓库提供以下模板：

- `templates/custom-properties/part.prtprp`：零件。
- `templates/custom-properties/asem.asmprp`：装配体。
- `templates/custom-properties/draw.drwprp`：工程图。

`scripts/custom-properties.schema.csv` 是属性预览/同步脚本及 VBA 宏使用的保留字段清单。正式应用时会删除文件级中未列入 CSV 的属性；当前 `v1.0.3` 默认 CSV 尚未包含二维码字段。需要保留现有二维码属性时，应先按当前模板的实际属性名补充：

```csv
All,has_QRcode,0
Assembly,QR_num,0
Part,号数30,0
All,QR_size,
All,QR_spacing,0
```

先运行只读预览并检查删除/新增清单，再决定是否应用：

- PowerShell 入口：`scripts/Preview-CustomProperties.cmd`。
- VBA 预览宏：`macros/PreviewCustomProperties.swb` 或 `macros/PreviewCustomProperties.bas`。
- VBA 应用宏：`macros/ApplyCustomProperties.bas`。应用前会检查文件状态并创建备份，但仍应人工确认预览结果。

## 输出

```text
asset-library/
  asset-registry.json
  <asset-uuid>/v<asset-version>/
    asset_<uuid>_v<version>.json
    geometry/model.step
    geometry/model.stl
    source/models/...
    drawings/source/...
    drawings/pdf/...

project-export/
  <assembly-uuid>/v<assembly-version>/
    assembly_<assembly-uuid>_v<version>.xml
    meshes/<project-unit-uuid>/model.step
    meshes/<project-unit-uuid>/model.stl
    export-report.json
```

Project 单元永远同时生成 STEP 和 STL；窗口中的格式选项只决定 XML 的 `mesh file` 引用哪个格式。Project 不生成 SLDPRT/SLDASM、SLDDRW、PDF 或 Asset manifest。

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

从 GitHub Releases 下载 `SolidWorksAssetExporter-v1.0.3.zip` 并完整解压。关闭 SOLIDWORKS，右键解压目录中的 `Install.cmd`，选择“以管理员身份运行”。如果从源码目录安装，则运行：

```text
.\scripts\install.cmd
```

重新启动 SOLIDWORKS 后，从 Add-in 菜单启用 `Asset / Project 混合导出`，再执行 `Asset / Project 导出` 命令。首次使用先设置 Asset 库、Project 根目录和 XML mesh 格式，然后点击“分类预览”。图纸只通过 SOLIDWORKS 查找模型同目录、同文件名的 SLDDRW，不再设置额外图纸搜索目录。

卸载前关闭 SOLIDWORKS，右键发布包中的 `Uninstall.cmd` 并选择“以管理员身份运行”。如果从源码目录卸载，则运行：

```text
.\scripts\uninstall.cmd
```

默认安装位置是 `%ProgramData%\SolidWorksAssetExporter`。设置保存在当前用户 `%APPDATA%\SolidWorksAssetExporter\settings.json`，卸载脚本不会删除用户设置或任何导出数据。

## 现场验收清单

在 SOLIDWORKS 2025、2026 各执行一遍：

1. 编译、安装、启动、启用 Add-in、卸载。
2. `Root → 非Asset装配 → Asset + 非Asset装配`：父节点为 Group；Asset 和同级 Project 都是叶节点。
3. 第三级 Asset：只展开包含 Asset 的分支；其他分支在各自最高无 Asset 根处折叠。
4. 整机无 Asset：XML 只有一个顶层 Project，且只有一对 STEP/STL。
5. 顶层为 Asset：XML 只有一个 Asset；通过调试/日志确认分类扫描没有调用其 `GetChildren`。
6. 复用同一 Asset 版本；修改源模型但不提升版本时必须拒绝。
7. Project STEP/STL 均存在；切换格式只改变 XML 的 mesh 引用。
8. 在两个导出模型中检查原点；用 XML 位姿重建装配并与 SOLIDWORKS 比较。
9. 零件 Asset 的 `source/models` 只包含自身 SLDPRT；装配体 Asset 只包含根及向下层级内的 SLDASM/SLDPRT，且能在隔离目录打开；确认父装配体、同级分支和 Project 文件均未混入。
10. 检查 Asset 根模型及层级内每个子装配体、零件同目录、同文件名的直接关联 SLDDRW 和全页 PDF；找不到时按无图纸处理，无关图纸和其他目录中的图纸不得被导出。
11. 人工制造中途失败，确认 staging 被清理且既有版本目录未被覆盖。
12. 导出前后确认模型活动配置和显示状态未被切换，并确认选择和 STEP/STL 系统选项已恢复。

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
- [SetDocumentSaveToNames 文件筛选](https://help.solidworks.com/2026/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IPackAndGo~SetDocumentSaveToNames.html)
- [PDF SetSheets](https://help.solidworks.com/2026/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IExportPdfData~SetSheets.html)
- [Referenced Documents 搜索目录](https://help.solidworks.com/2023/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ISldWorks~GetSearchFolders.html)
