# 发布与安装

## 本地制作发布包

构建默认使用仓库 `third_party\solidworks` 中的官方 Interop DLL，构建机无需安装 SOLIDWORKS：

```powershell
.\scripts\build-addin.ps1 -Configuration Release
.\scripts\new-release-package.ps1 -Version v1.0.1
```

生成的 `artifacts\SolidWorksAssetExporter-v1.0.1.zip` 包含插件 DLL、SOLIDWORKS Interop DLL 与 CMD 安装、卸载脚本。

## 在目标主机安装

目标主机需要安装 64 位 SOLIDWORKS 和 .NET Framework 4.8。关闭 SOLIDWORKS 并完整解压发布包，然后右键 `Install.cmd`，选择“以管理员身份运行”。

安装脚本直接使用发布包内的 DLL，不需要 PowerShell，也不需要用户指定 SOLIDWORKS DLL 路径。管理员权限仍是注册 COM Add-in 和写入 `%ProgramData%` 的必要条件。

卸载前关闭 SOLIDWORKS，然后右键 `Uninstall.cmd`，选择“以管理员身份运行”。

## GitHub Actions 发布

推送格式为 `v*` 的 tag 后，GitHub 托管的 Windows Runner 会自动执行生产构建、创建 ZIP 并发布 GitHub Release：

```powershell
git tag v1.0.1
git push origin v1.0.1
```

该流程不需要自托管 Runner，也不需要配置 `SOLIDWORKS_INTEROP_DIR`。
