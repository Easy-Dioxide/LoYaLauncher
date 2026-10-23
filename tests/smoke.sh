#!/bin/bash
# 冒烟测试：Xvfb 下运行 App，逐个点击 8 个导航页，截图 + 像素偏差分析
set -u
export DISPLAY=:99
APP_DLL=/workspace/LoyaLauncher/src/LoyaLauncher.App/bin/Release/net10.0/LoyaLauncher.App.dll
OUT=/tmp/smoke
mkdir -p $OUT

# 启动虚拟显示
pkill -f "Xvfb :99" 2>/dev/null
Xvfb :99 -screen 0 1100x720x24 &
XVFB_PID=$!
sleep 2

# 启动应用
cd /workspace/LoyaLauncher/src/LoyaLauncher.App/bin/Release/net10.0
dotnet LoyaLauncher.App.dll > /tmp/smoke/app.log 2>&1 &
APP_PID=$!
sleep 6   # 等启动 + 入场动画

# 导航项中心坐标（经侧栏像素剖面实测：标签中心 y，按钮 x=12..220）
# 顺序：Home, Versions, Mods, Modpacks, Accounts, Downloads, Java, Settings
declare -a NAMES=(Home Versions Mods Modpacks Accounts Downloads Java Settings)
declare -a YS=(162 188 211 236 260 284 307 332)

for i in "${!NAMES[@]}"; do
    name=${NAMES[$i]}
    y=${YS[$i]}
    xdotool mousemove 116 $y click 1
    sleep 1.4   # 页面过渡动画 0.3s + 余量
    import -window root $OUT/${name}.png
    echo "captured ${name}"
done

# 账户页微软登录面板特写（Accounts 页截图中已含）；Java 页下载区在同名截图中
kill $APP_PID 2>/dev/null
kill $XVFB_PID 2>/dev/null
echo "done"
