# v1.0.7 发布教程

## 1. 发布前检查

发布必须从已经合并并通过 CI 的 `main` 创建，不能直接从尚未合并的功能分支打标签：

```powershell
git switch main
git pull --ff-only origin main
git status
```

先在当前 PowerShell 会话设置准备发布的版本。以后升版只需先修改这一项，并同步程序集版本与 README 中的当前版本：

```powershell
$Version = 'v1.0.7'
```

确认工作区干净，并检查仓库中没有遗漏的旧发布版本：

```powershell
rg "v1\.0\.4|1\.0\.4\.0" README.md docs src
```

上述命令不应找到仍需更新的旧发布版本。历史变更记录中的旧版本号可以保留。

## 2. 构建和验证

构建默认使用 `third_party\solidworks` 中的官方 Interop DLL，构建机无需安装 SOLIDWORKS：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-core.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\verify-addin-contract.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\build-addin.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\new-release-package.ps1 -Version $Version
```

契约验证使用内嵌测试桩，并隔离输出到 `src\SolidWorksAssetExporter.AddIn\bin\Contract-Release`；该目录中的 DLL 绝不能安装或发布。生产 `build-addin.ps1` 使用 `third_party\solidworks` 中的真实 Interop。`new-release-package.ps1` 会读取 Add-in 元数据：缺少任一真实 Interop 引用，或 DLL 内定义了测试桩 `ISwAddin` 时立即拒绝打包。

生成的安装包为：

```text
artifacts\SolidWorksAssetExporter-v1.0.7.zip
```

完整解压 ZIP，确认根目录至少包含：

```text
README.md
RELEASING.md
Install.cmd
Uninstall.cmd
set-interactive-user-startup.ps1
payload\SolidWorksAssetExporter.AddIn.dll
payload\SolidWorksAssetExporter.Core.dll
Install-CustomPropertyTemplates.ps1
custom-properties\part.prtprp
custom-properties\asem.asmprp
custom-properties\draw.drwprp
custom-properties\properties.txt
macros\MigrateFixtureAcceptsInterfaces.swb
macros\MigratePlacementToConnectionInterface.swb
```

## 3. 提交 v1.0.7 改动

在发布分支提交并通过 PR 合并到 `main`。不要把 `artifacts/` 构建产物提交进 Git：

```powershell
git add README.md docs .github src scripts tests
git status
git commit -m "release: prepare v1.0.7"
git push
```

PR 标题建议使用 `release: prepare v1.0.7`，正文粘贴 README 中的“v1.0.7 升版说明”，并列出自动测试、生产构建和 SOLIDWORKS 现场验证结果。

## 4. 创建标签并自动发布

PR 合并后重新同步 `main`，确认 `v1.0.7` 标签尚不存在：

```powershell
git switch main
git pull --ff-only origin main
git tag --list v1.0.7
```

没有输出时创建并推送带说明的标签：

```powershell
git tag -a v1.0.7 -m "SolidWorks Asset Exporter v1.0.7"
git push origin v1.0.7
```

推送 `v*` 标签后，GitHub Actions 会自动执行生产构建、制作 ZIP，并创建 GitHub Release。不要在推送标签前手工创建同名 Release。工作流支持安全重跑：如果 Release 已存在，会覆盖上传同名安装包，不会再次创建 Release。

## 5. 填写 GitHub Release 说明

Release 标题使用：

```text
SolidWorks Asset Exporter v1.0.7
```

升版说明：

- SOLIDWORKS 本地整机导出统一命名为 `assembly_package`（装配包），界面、预览、错误提示、README 和属性协议同步更新。
- Wanxiang `Project`、远端 `projects/`、上传 API 和 `settings.json` 兼容配置键保持不变；assembly_package 上传后映射为 Wanxiang Project。装配 XML 根属性使用 `mesh_format`，普通几何节点统一使用 `kind="mesh"`，不再输出 `project_mesh_format` 或 `kind="project"`。
- 同步 Wanxiang 0.4.0：Asset 改用 `PUT /asset/{uuid}/v{version}` 携带完整 ZIP 和 `X-Content-Fingerprint` 原子发布；本地 assembly_package 作为 Wanxiang Project 保持 `/archive/projects/...`。
- Asset UUID 升级为 v2 规则，新增持久 SHA-256 缓存及远端版本判断。
- `class=robot` 改为 assembly_package XML 的 `robot_id` 地址引用；不生成或注册 Asset，也不生成几何、源模型或图纸。
- 新增可关闭的 assembly_package 导出；关闭后仍正常导出非 Robot Asset，且不上传 Wanxiang Project。
- 分类和 manifest 统一只读取文件级“自定义”属性，并一次性汇总缺失或空白的必填字段。
- Wanxiang 发布增加逐项进度、HTTP 响应诊断和不泄漏 API key 的本地日志。
- 非 Asset 装配体递归拆分至叶节点，Asset 继续作为硬边界。
- 加强轻量化、虚拟/内嵌组件、模型窗口和 Pack and Go 隔离处理。
- 分类预览新增源文件快照、变化复核、阶段进度和取消。
- Asset 图纸改为只收集原始 SLDDRW，不再生成 PDF。
- 新增 Asset 属性检查脚本和批量补填零件名、统一 Asset 版本的 SOLIDWORKS 宏。
- 新增快换盘、快换支架和机械接口属性，删除已废弃的 `accepts_robots`。
- 发布包附带旧 `accepts_interface`/`placement_interface` 到新接口字段的安全迁移宏。
- 安装程序自动为当前桌面用户启用 Add-in。
- 发布构建隔离契约测试桩，并在生成 ZIP 前校验真实 SOLIDWORKS Interop，避免无效 Add-in DLL 被分发。

Robot 不调用 Wanxiang Asset 发布接口；assembly_package 上传后的 Wanxiang Project 消费端需要支持 `<mesh robot_id="设计目的:asset_version" />` 地址引用，例如 `<mesh robot_id="Hebe:1" />`。

## 6. 发布后验收

1. Release 的 tag 和目标提交必须是 `v1.0.7` 对应的 `main` 提交。
2. Assets 中必须包含 `SolidWorksAssetExporter-v1.0.7.zip`，不能只有 GitHub 自动生成的 Source code。
3. 在干净目录完整解压安装包，关闭 SOLIDWORKS，以管理员身份运行 `Install.cmd`。
4. 启动 SOLIDWORKS，确认 `Asset / assembly_package 混合导出` 已启用并能打开导出窗口。
5. 检查 `%LOCALAPPDATA%\SolidWorksAssetExporter\addin.log`，确认本次启动新增 `ConnectToSW succeeded.`。
6. 完成分类预览、Asset/assembly_package 本地导出和 Wanxiang 上传现场验证。

## 7. 卸载

关闭 SOLIDWORKS，然后右键安装包中的 `Uninstall.cmd`，选择“以管理员身份运行”。用户设置和导出数据不会被删除。
