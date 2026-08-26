SOLIDWORKS 自定义属性预览宏
==========================

文件
----
PreviewCustomProperties.swb     SOLIDWORKS VBA 文本宏（仅预览）
custom-properties.schema.csv    需要保留的字段清单

运行
----
1. 在 SOLIDWORKS 中打开总装配体。
2. 选择“工具 > 宏 > 运行”。
3. 文件类型选择“所有文件”或 SOLIDWORKS 宏，选择 PreviewCustomProperties.swb。
4. 宏询问 CSV 路径时，粘贴 custom-properties.schema.csv 的完整路径。
5. 宏在浏览器中打开 HTML 预览报告。

报告
----
红色 − 属性：正式同步时将删除。
绿色 + 属性：正式同步时将新增。
UNCHANGED：属性字段已经符合清单。

安全
----
此宏只有读取和生成 HTML 报告的逻辑，不包含属性新增、删除或模型保存调用。
它不会修改或保存任何 SLDPRT/SLDASM 文件。

首次运行 .swb 时，SOLIDWORKS 可能自动把它转换为 .swp；这是正常行为。
