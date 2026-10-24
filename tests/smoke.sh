#!/bin/bash
# 冒烟测试：Xvfb 下运行 App，自适应检测侧栏导航位置并逐个点击 8 个页面，
# 截图 + 像素偏差分析。导航坐标由截图像素聚类实时探测，改按钮尺寸无需改脚本。
set -u
export DISPLAY=:99
APP_DIR=/workspace/LoyaLauncher/src/LoyaLauncher.App/bin/Release/net10.0
OUT=/tmp/smoke
mkdir -p $OUT

pkill -f "Xvfb :99" 2>/dev/null
Xvfb :99 -screen 0 1100x720x24 >/dev/null 2>&1 &
XVFB_PID=$!
sleep 2

cd $APP_DIR
dotnet LoyaLauncher.App.dll > /tmp/smoke/app.log 2>&1 &
APP_PID=$!
sleep 6   # 等启动 + 入场动画

import -window root $OUT/Home.png

# ---- 自适应探测导航项中心 y（侧栏 x=30..215 内的亮文字行聚类）----
mapfile -t CENTERS < <(python3 - <<'EOF'
from PIL import Image
img = Image.open('/tmp/smoke/Home.png').convert('RGB')
px = img.load()
rows = []
for y in range(100, 540):
    bright = sum(1 for x in range(30, 215) if sum(px[x, y]) > 300)
    rows.append((y, bright))
groups, cur = [], None
for y, b in rows:
    if b > 3:
        cur = [y, y] if cur is None else [cur[0], y]
    else:
        if cur and cur[1] - cur[0] >= 4: groups.append(tuple(cur))
        cur = None
if cur and cur[1] - cur[0] >= 4: groups.append(tuple(cur))
# 取最大的 8 组（导航项），忽略 logo/底部区域的文字
groups = sorted(groups, key=lambda g: g[1] - g[0], reverse=True)[:8]
groups.sort()
for g in groups:
    print((g[0] + g[1]) // 2)
EOF
)
echo "探测到 ${#CENTERS[@]} 个导航项: ${CENTERS[*]}"

declare -a NAMES=(Home Versions Mods Modpacks Accounts Downloads Java Settings)
for i in "${!NAMES[@]}"; do
    [ $i -lt ${#CENTERS[@]} ] || break
    xdotool mousemove 120 "${CENTERS[$i]}" click 1
    sleep 1.4
    import -window root $OUT/${NAMES[$i]}.png
    echo "captured ${NAMES[$i]} (y=${CENTERS[$i]})"
done

kill $APP_PID 2>/dev/null
kill $XVFB_PID 2>/dev/null
echo "done"
