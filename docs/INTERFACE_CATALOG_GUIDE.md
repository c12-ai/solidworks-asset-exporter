# 接口候选库与只读属性模板

接口候选库由配置的 Wanxiang 数据服务集中保存。连接相同服务的客户端通过承载关系面板的“接口属性 / 建议…”或 MCP 读取和添加候选。

## 旧属性保留

插件继续读取已有文件级 `connection_interface`、`accepts_interfaces` 和 `slots_num`。已有接口名称继续按原字符串匹配。旧候选文件 `%APPDATA%\SolidWorksAssetExporter\interfaces.json` 仍可用于离线查询和迁移。

## 命名方式

采用 `类别.规格[.关键尺寸][.厂商或型号]`，例如用户指定的 `试管.15ml.D20`。容量统一使用 `ml` 或 `ul`，尺寸默认 mm，`D` 表示事先约定的圆形定位/承载部位直径。可追加 `H120` 或 `底座L80xW60` 等字段，但这些只是你确认的尺寸定义，不是插件测量结果。

候选库包含 103 个基础名称，覆盖试管、离心管、冻存管、锥形瓶、培养瓶/皿、试剂瓶、储液槽、孔板、PCR、枪头/盒、移液管、架/底座、托盘/载台、快换及工具安装。`A型`/`B型` 是可自行定义的接口系列，不是厂商通用标准。

这些是录入候选，不是经过验证的兼容性数据库：相同容量或孔数的不同厂家可能具有不同外形、口径和定位结构。采用基础名称前应确认它在项目内代表同一种实际接口；有差异时增加尺寸或型号，分别命名。没有尺寸的候选没有暗含默认直径。`试管.15ml.D20` 是命名示例，不声明所有 15 ml 试管直径均为 20 mm。

父级 `accepts_interfaces` 中的值必须与子级 `connection_interface` 一致，包括大小写和单位写法。多个接受接口在模型中使用英文分号分隔，在 MCP 中通过数组传入。`slots_num` 记录包含空槽的总容量。

## 使用

接口目录只维护机械接口。容纳能力用容器的布尔值 `can_hold_contents` 表达。客户端会隐藏已知的旧内容物候选，并拒绝新增这些名称。

Agent 用 `interface_catalog` 查询候选，必要时用 `interface_catalog_add` 加入已确认的名称，再通过 `asset_update_interfaces` 应用到资产。读取结果包含 `interfaces`、`source` 和离线时的 `warning`。旧候选可以读取本机 JSON 的 `interfaces` 数组，作为 `interface_catalog_add` 的 `names` 参数批量迁入。详见 [MCP 使用指南](MCP.md)。

在线库使用 Wanxiang 已配置的地址和凭据，读写 NAS 上的 `asset-interface-catalog/v2/`。每个名称保存在一个 UTF-8 JSON 文件中，内容为 `{"schema_version":"1.0","name":"试管.15ml.D20"}`，文件名为接口名称的 SHA-256 加 `.json`。`.json` 属于 `wanxiang-sync` 的已知分类。重试同名请求是幂等的，不同名称的并发新增互不覆盖。网络故障时可继续查离线候选，新增失败会返回错误。

## 模板修改范围

零件 `part.prtprp` 和装配 `asem.asmprp` 按基础属性、资产角色、接口与槽位、二维码、设计说明排列。选“是”才显示资产分组。选择 Robot 显示外部引用版本。勾选 `is_quick_changer` 显示侧别，勾选 `has_QRcode` 显示二维码数量、尺寸和间距。非资产装配体显示工位选项，勾选 `is_workstation` 后显示工位名称。

自由文本只开放 `设计目的`、`设计原理` 和 `升版说明`。机械接口和设计槽数在插件维护。`slot_type` 和 `engagement` 在属性面板中使用固定选项下拉框。SOLIDWORKS 原生模板的数字控件不支持按槽数隐藏下拉框。槽数为 0 或空白时，`slot_type` 可留空，或在原生列表选“不适用（无槽）”。有槽时请选择实际槽型。 [SOLIDWORKS 数字控件说明](https://help.solidworks.com/2025/English/SolidWorks/oh_propertytabbuilder/r_Number.htm)

插件的“接口属性 / 建议…”在槽数大于零时显示槽位类型。两个入口读取和写入同一份 SOLIDWORKS 文件级自定义属性。打开插件编辑器时读取当前文件属性。若在其它入口修改了已打开的资产，可在插件中重新读取文件属性。完成原生编辑后保存源文件，使更改持久化。

## 应用补充包

`Install-InterfaceOptions.ps1` 更新现有模板中的相关控件，并合并当前 Windows 用户的候选名称。默认仅预览，指定 `-Apply` 才实际更新。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-InterfaceOptions.ps1 -TemplateDirectory "C:\C12_SolidWorksTemplate\自定义属性"
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-InterfaceOptions.ps1 -TemplateDirectory "C:\C12_SolidWorksTemplate\自定义属性" -Apply
```

更新前备份所有将改写的文件，输出备份目录及 `manifest.json`；其中记录原路径、原哈希和是否原本存在。接口库使用与插件一致的互斥文件锁及临时文件替换，模板按文件替换；若中途失败，已成功更新的文件会保留改动，按备份清单恢复即可，不承诺跨文件原子更新。

在自定义属性任务窗格重新选择模板或按 F5 刷新。模板按文档类型显示的机制见 [SOLIDWORKS 官方说明](https://help.solidworks.com/2022/english/SolidWorks/oh_propertytabbuilder/t_tab_builder_creating_tab.htm)。
