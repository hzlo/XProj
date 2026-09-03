# 数据同步

## 主要功能
- 使用webdav进行数据同步
- 支持间隔时长自动同步
- 手动同步
- 差异化同步

## 同步范围
- 仅同步应用数据目录下的配置文件（顶层 *.json，如 data.json、translator.json）
- 不同步备份（data.backup-*.json、data.invalid-*.json）
- 不同步日志、笔记等子目录内容
- 自动排除同步清单（.xproj-sync.json）、同步插件自身配置（data-sync.json）、更新缓存（update-cache.json）
- 本地目录固定为应用数据目录，无需手动选择
