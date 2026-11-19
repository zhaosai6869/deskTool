# todesk

保存 ToDesk 设备代码与连接密码，双击自动发起远控连接。需要 ToDesk for Windows 4.0.0 或更高版本。

## 打开方式
从主界面点击「💻 todesk」卡片进入。

## 准备：从 ToDesk 获取信息
- 「设备代码」：被控设备的代码，不带空格。
- 「连接密码」：填安全密码或临时密码。
- 设备必须已在 ToDesk 设备列表中「解锁」，否则无法发起控制。

## 新增记录
1. 点「新增」→ 弹出「编辑 ToDesk」窗口。
2. 填「名称」「设备代码」「连接密码」「备注」。
3. 点「确定」保存。

## 发起连接
- 双击表格某行：程序拉起 `ToDesk.exe`，检测并启动后台服务，通过命令行发起连接，成功后把远端会话窗口切到前台。
- 连接命令失败会自动重试 3 次。全过程写日志到 `data\todesk_connect.log`，出问题可查该文件。

## 右键菜单
- 「复制密码」/「复制信息」（名称、设备代码、连接密码、备注）。

## 数据保存位置
SQLite 表 `ToDeskRecords`；连接日志 `data\todesk_connect.log`；密码用 DPAPI 加密保存。

> 提示：依赖后台服务 `ToDesk Service`。若服务未启动，程序会弹 UAC 提权；如仍失败，可手动用管理员命令启动：
> `sc config ToDesk_Service start= auto` 然后 `sc start ToDesk_Service`
