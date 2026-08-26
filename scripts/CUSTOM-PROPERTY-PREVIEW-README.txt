SOLIDWORKS 自定义属性同步预览包
================================

用途
----
只预览当前总装配体、子装配体和零件的属性同步结果。
本入口不会修改或保存任何 SLDASM/SLDPRT 文件。

操作步骤
--------
1. 使用 Excel 或记事本打开 custom-properties.schema.csv。
2. 按实际要求填写字段清单并保存 CSV：
   DocumentType,Name,DefaultValue
   All,b,
   All,c,
   All,d,

   DocumentType 可选：
   All      零件和装配体都需要
   Part     仅零件需要
   Assembly 仅装配体需要

3. 启动 SOLIDWORKS，打开需要检查的总装配体。
4. 双击 Preview-CustomProperties.cmd。
5. 查看 PowerShell 输出：
   CHANGE    文件需要调整
   UNCHANGED 文件不需要调整
   - name    正式同步时会删除的属性
   + name=   正式同步时会新增的属性

安全说明
--------
Preview-CustomProperties.cmd 没有使用 -Apply 参数，只执行预览。
不要自行给命令添加 -Apply，除非已经备份并确认准备正式修改模型。

