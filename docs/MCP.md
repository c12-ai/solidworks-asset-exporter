# SOLIDWORKS Asset Exporter MCP

## Codex 安装与连通性验证

按 [Windows 快速安装](../README.md#windows-快速安装) 安装 SOLIDWORKS 插件后，在同一 Windows 用户和桌面会话中运行 `Install-Codex.cmd`。脚本定位当前注册的插件，在 Codex 用户配置中登记 `solidworks-assets`，并安装配套技能。标准 MCP 程序位于 `%ProgramData%\SolidWorksAssetExporter\SolidWorksAssetExporter.Mcp.exe`。按用户更新时，MCP 程序与当前注册的 Add-in DLL 位于同一个版本目录。

打开一份已保存的 SOLIDWORKS 总装。在解压目录运行 `.\Install-Codex.cmd -Check -ProbeSolidWorks`，可以只读检查已安装的 MCP、技能和 SOLIDWORKS 连接。用 `codex mcp get solidworks-assets` 查看登记细节，重启 Codex 后调用 `assembly_context`。返回当前总装的 `document_path` 和 `configuration` 表示插件管道已连通。若无法连接，先确认 SOLIDWORKS 插件已加载、Codex 和 SOLIDWORKS 使用同一个 Windows 用户，且配置指向和当前 Add-in 配套的 MCP 可执行文件。

首次登记设置 30 分钟工具超时。更新已有登记时保留已有参数和超时。如果插件装在自定义位置，可用 `Install-Codex.cmd -McpExecutable <完整路径>` 明确指定。对于已有的按用户 COM 安装，机器级安装完成后使用 SOLIDWORKS 实际加载的版本核对 MCP 路径。已替换的 Codex 配置和技能文件备份在 Codex 用户目录的 `solidworks-asset-exporter-backups` 下。

如需手工登记，可在 Codex 用户配置 `config.toml` 中填写以下内容。使用 PowerShell 运行 `codex mcp add solidworks-assets -- "$env:ProgramData\SolidWorksAssetExporter\SolidWorksAssetExporter.Mcp.exe"` 也可登记标准安装。手工更新已有登记时请保留原来的可选配置。

```toml
[mcp_servers.solidworks-assets]
command = 'C:\ProgramData\SolidWorksAssetExporter\SolidWorksAssetExporter.Mcp.exe'
args = []
tool_timeout_sec = 1800
```

其他支持 stdio 的 MCP 客户端可使用各自的 JSON 配置格式。例如：

```json
{
  "mcpServers": {
    "solidworks-assets": {
      "command": "C:\\ProgramData\\SolidWorksAssetExporter\\SolidWorksAssetExporter.Mcp.exe",
      "args": []
    }
  }
}
```

多个 SOLIDWORKS 进程同时运行时，在 `args` 中指定 `["--pid", "12345"]`。大型装配导出时，根据客户端配置延长工具超时时间。通信通过标准输入输出和当前用户的本地命名管道，SOLIDWORKS 操作由插件调度到其界面线程。

## 工具

| 工具 | 功能 |
| --- | --- |
| `assembly_context` | 读取活动装配、配置和保存状态。 |
| `asset_list` | 列出资产实例、源文件、用途、接口、槽位容量、内容物能力和版本。 |
| `component_list` | 盘点当前配置的可见组件，包括未分类零部件、子装配体和文件级属性。 |
| `asset_properties_plan` | 预检资产边界、主分类和自定义属性，列出同源实例和接口缺项。 |
| `asset_properties_apply` | 重新校验计划并备份、保存源文件，维护普通资产的版本。 |
| `binding_evidence` | 分页读取资产和 CAD 证据，包括 CAD 上级、世界坐标原点、可读取的双端配合和顶层固定状态。 |
| `binding_list` | 读取当前配置的显式承载关系和校验结果。 |
| `binding_plan` | 预检一个父级与其子实例的新增关系，返回拟写入属性和 `plan_token`。 |
| `binding_apply` | 校验计划有效性，写入接口和必要版本，再保存承载关系。 |
| `binding_add` | 接口已就绪时直接追加并保存显式关系。 |
| `asset_update_interfaces` | 显式维护单个源文件的接口或总槽位。 |
| `interface_catalog` | 读取 Wanxiang 在线接口候选库。离线时读取本地候选并返回提示。 |
| `interface_catalog_add` | 向在线接口候选库追加已确认的接口名称。 |
| `asset_export_preview` | 预检导出范围、数据校验结果和版本。 |
| `asset_export` | 本地导出。传入 `upload: true` 时发布到万象。 |

## 新项目

配套 [Agent 技能](../skills/solidworks-asset-exporter/SKILL.md) 覆盖从零盘点零部件、判断独立资产、填写自定义属性、建立承载关系和验证发布结果。

先用 `component_list` 盘点资产和未分类的零部件。现有 Asset 装配体是遍历边界。清单通过 `classification` 区分 `asset`、`workstation`、`non_asset`、`unclassified` 和无法读取的 `unavailable`。结合项目中独立部署、抓取、使用或维护的边界来判断 `is_asset`，然后按实际用途填写 `class` 和业务角色。新增 Asset 时提供 `零件名` 和 `设计目的`。资产边界变更后重新盘点。

`asset_properties_plan` 接收装配上下文、`instance`、`source_path` 和 `properties` 对象。用原参数加返回的 `plan_token` 调用 `asset_properties_apply`。字段按源文件保存，同源实例共享。接口缺项会出现在 `interface_issues`，后续需按设计依据补齐。已有绑定端点和跨配置影响会在预检中检查。属性示例和分类口径见上面的 Agent 技能。

可填写字段包括 `is_asset`、`class`、`零件名`、`设计目的`、`设计原理`、`升版说明`、`is_workstation`、`workstation_name`、`is_tool`、`is_fixture`、`is_quick_changer`、`quick_changer_side`、`is_quick_changer_rack`、`is_adjustable`、`can_hold_contents`、`slot_type`、`engagement`、`has_QRcode`、`QR_num`、`QR_size`、`QR_spacing`、`connection_interface`、`accepts_interfaces` 和 `slots_num`。在 `properties` 对象中，多值接受接口使用分号分隔。布尔字段可传 JSON 布尔值、`0`/`1` 或相应字符串。`asset_list` 以布尔值返回 `can_hold_contents`。普通资产版本由插件维护，Robot 可指定已发布的外部引用版本。

资产描述只填写硬件用途、规格、容量与接口。维护已有资产时用 `asset_properties_plan` 清理描述中的内容物词条，容纳能力用布尔值 `can_hold_contents` 表达。凡声明机械 `accepts_interfaces` 的资产，都要核实其自身的 `connection_interface`，包括按安装基准定义的固定工位接口。已有真实的子级机械接口应保留。

`binding_evidence` 的每项包含 `asset` 和 `evidence`。配合列出当前可读取的双端资产实例和 SOLIDWORKS 配合类型代码，部分轻量化或嵌套组件可能缺少配合证据。结合图纸、接口表和实际安装用途判断业务关系。参见 [SOLIDWORKS GetMates API](https://help.solidworks.com/2019/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IComponent2~GetMates.html)。

`binding_plan` 接收装配上下文、`parent` 和 `children`。子资产缺接口时可传入已确认的 `connection_interface`，插件将子接口追加到父级的兼容列表。计划返回源文件声明或显式传入的设计容量 `slots_num`、当前父实例已绑定槽数 `occupied_slots`、同源实例观察到的最大绑定槽数 `required_slots`、文件级变更、关系预检和令牌。已独立确认设计容量时可传 `slots_num`。用同一组参数加令牌调用 `binding_apply`。插件在写入前重新校验，在适用时查询发布版本。多文件写入发生中断时，工具会报告已保存的源文件。重新读取当前状态并生成新计划后可以继续。

通过 `asset_properties_plan` 和 `asset_properties_apply` 维护文件级自定义属性。有设计槽位时填写 `slot_type`。普通承托槽选 `place`，其余按机构选 `mount`、`clamp`、`thread`、`press_fit` 或 `snap_fit`。无槽和容量未声明时省略 `slot_type`。`engagement` 可独立填写，按接合约束选 `loose` 或 `tight`。实际槽位和设计容量各按证据填写。`binding_plan`、`binding_apply` 和 `binding_list` 处理装配中的父子端点、槽位与安装位姿。实例的实际接合与松开由消费端控制。

每个子实例只绑定一个父级。需要为另一父级创建一个同源、同位姿实例，或修正历史多父关系时，用 `binding_duplicate_plan` 传入装配上下文、`parent`、单个 `child` 和可选的 `reuse_slot_from_binding_id`。核对复制及槽位计划后，用同样参数加 `plan_token` 调用 `binding_duplicate_apply`。已有目标父级关系迁移到复制实例时保留原绑定和槽位标识。

## Agent 操作顺序

1. 调用 `assembly_context` 读取活动装配的 `document_path` 和 `configuration`。其余装配工具均传入这两个值，切换文档或配置后重新读取。开始写入前保存 CAD 修改。
2. 用 `asset_list` 查找资产。用返回的 `instance` 区分同一源文件的不同实例，维护属性时同时传入 `source_path`。根资产的 `instance` 为空字符串。列表支持 `offset` 和 `limit`。
3. 用 `asset_update_interfaces` 保存子资产的 `connection_interface` 和父资产的 `accepts_interfaces`。已知父资产包含空槽时，也可填写设计总容量。省略的字段保留原值，空字符串和空数组可以清除可选接口。源文件属性作用于该文件的所有实例。普通资产当前版本已发布时，插件查询 Wanxiang 并分配新版本。Robot 的外部引用版本由其 URDF 发布方决定。
4. 接口已就绪时可用 `binding_add` 传入 `parent` 和 `children`，需要核对属性改动时用 `binding_plan` 和 `binding_apply`。已有不同父级的子实例须先确认是原位复制还是更换父级。用 `binding_list` 检查端点、接口和槽位。对已有绑定的父资产，`asset_update_interfaces` 可设置 `sync_accepts_from_bindings=true`，追加已绑定子资产声明的接口。
5. 用 `asset_export_preview` 检查导出计划和 `placement_diagnostics`。修正同款门的多状态和多父关系，人工核对拆分原点重合、同槽位姿偏差、容量与占用差异。用 `asset_export` 保存必要的版本变更并导出，工具返回本地目录、资产数量及版本变更。显式传入 `upload: true` 时，工具使用插件保存的 Wanxiang 设置上传，并单独返回上传结果或错误。

`binding_add` 和 `binding_apply` 分别记录承载关系与已声明的容量。父资产的 `slots_num` 包含暂时空置的设计槽位，容量未知时可留空。同源且引用相同 CAD 配置的父资产可用一个满载代表做容量核对。观察到的超容量会逐实例报告。接口候选可通过 `interface_catalog` 查询，通过 `interface_catalog_add` 追加经过确认的名称。

复用物理槽位时，在 `binding_list` 找到同一父组件下已有关系的 `id`，作为 `reuse_slot_from_binding_id` 传给 `binding_add`，或同时传给 `binding_plan` 和 `binding_apply`。先在 CAD 中把新子组件定位到同一槽位的正确安装位姿。复用不会增加 `slots_num`。`binding_list` 对共享槽位返回 `slot_id`、`slot_pose` 和 `slot_offset`。首次作为参考的子组件偏移为零平移和单位旋转，新增子组件的偏移按 CAD 位姿计算。MCP 位姿的平移单位为米，四元数顺序为 `xyzw`。

导出工具使用插件保存的 Asset 库目录、装配包目录和 Wanxiang 凭据。初次使用时在导出窗口保存这些设置。默认根据已保存设置决定是否导出装配包，也可用 `export_project` 覆盖。装配包版本根据配置的本地装配包目录和内容指纹维护，普通资产版本以 Wanxiang 注册表为准。

## 属性模板

安装包中的 `Install-CustomPropertyTemplates.ps1` 可部署新版模板。已经定制过模板时，可用 `Install-InterfaceOptions.ps1 -TemplateDirectory <目录> -Apply` 就地更新接口和槽位容量控件。零件和装配面板仅开放 `设计原理`、`设计目的` 和 `升版说明` 的自由文本输入。原生面板和插件的槽位类型、接合方式下拉选择项写入同一份文件级自定义属性。普通资产及装配包版本由插件维护。 [接口库与模板说明](INTERFACE_CATALOG_GUIDE.md)
