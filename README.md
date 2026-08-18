# SOLIDWORKS Asset / Project 混合导出插件

这是一个面向 SOLIDWORKS 2025/2026、.NET Framework 4.8、64 位进程的 C# COM Add-in。它把当前装配体拆成三种节点：

- `Asset`：当前节点 `is_asset=true|1|yes`。这是硬终止边界，分类扫描不会读取其子节点，也不会在内部发现第二个 Asset。
- `Project`：当前节点的整棵可见子树都不含 Asset。整棵子树作为一个项目本地 STEP/STL 单元。
- `Group`：自身不是 Asset，但可见后代包含 Asset。只保留装配层级和位姿，没有 mesh。

这解决了“一次性定制件不是 Asset、但后续装配重建仍需要数模”的问题：它会作为最大无 Asset 子树的 `Project` 单元进入当前项目目录，不污染全局 Asset 库。

当前发布版本为 `v1.0.2`。

## 当前实现范围

- 两阶段分类和最大无 Asset 子树折叠。
- 分类扫描会过滤隐藏、抑制、包络组件；Asset 源文件收集只排除抑制组件，并保留层级内隐藏或包络模型的源文件。
- Asset 分类边界保持短路；源文件收集只从该 Asset 根节点向下遍历活动层级，不读取子节点的 Asset 语义，也不会生成子节点。
- UUIDv5、版本规则、同名属性冲突、已保存/无未保存修改校验。
- STEP AP214 和二进制 Fine STL 双格式导出；模型文档本地原点作为几何原点。
- XML 父子相对位姿、米、`qx/qy/qz/qw`、Asset ID 或 Project 相对 mesh 路径。
- Asset manifest、SHA-256、内容指纹、版本命中复用、同 ID 内容变化拒绝覆盖。
- 零件 Asset 只打包自身 SLDPRT；装配体 Asset 只打包根 SLDASM 及其层级内未抑制的子装配体/零件，不包含父装配体、同级分支或层级外依赖。
- 为上述每个 SLDASM/SLDPRT 收集直接关联的 SLDDRW，并导出对应源图纸和全页 PDF。
- Asset 与 Project 独立 staging，成功后目录级提交；既有版本不覆盖。
- 分类预览和确认窗口；导出模型文档当前活动配置及显示状态，不切换配置或显示状态；导出后恢复选择和 STEP/STL 全局设置。

核心层现有 20 项自动测试，Add-in 代码路径也可使用 `InteropStubs.cs` 做隔离契约构建；该 stub 只用于编译检查，生产构建不会包含它。

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

同一 `uuid + asset_version` 的源模型及图纸内容完全一致时，插件会校验现有文件并直接复用旧 Asset；如果内容已经变化，则拒绝用同一个版本号覆盖，必须提升 `asset_version`。当前不维护独立数据库，Asset 地址由资产库根目录、UUID 和版本确定，每个版本目录中的 manifest 是该 Asset 的文件与哈希记录。

Asset UUIDv5 的输入是文件名（含扩展名）、SOLIDWORKS 内部创建时间、模型文档当前活动配置、当前显示状态和文档类型。路径、`asset_version` 不参与 UUID，因此移动模型目录不会改变 UUID。同一零件的多个装配实例保留各自 XML 节点和位姿，但共享同一个 `asset_id`，Asset 包只创建或复用一次。`asset_id` 是 `<uuid>:<version>`，只出现在装配 XML；manifest 内只保存独立的 `uuid` 和 `version`。

## 输出

```text
asset-library/
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

从 GitHub Releases 下载 `SolidWorksAssetExporter-v1.0.2.zip` 并完整解压。关闭 SOLIDWORKS，右键解压目录中的 `Install.cmd`，选择“以管理员身份运行”。如果从源码目录安装，则运行：

```text
.\scripts\install.cmd
```

重新启动 SOLIDWORKS 后，从 Add-in 菜单启用 `Asset / Project 混合导出`，再执行 `Asset / Project 导出` 命令。首次使用先设置 Asset 库、Project 根目录、XML mesh 格式和额外图纸搜索目录，然后点击“分类预览”。

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
10. 检查 Asset 根模型及层级内每个子装配体、零件直接关联的 SLDDRW 和全页 PDF；无关图纸不得被导出。
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
