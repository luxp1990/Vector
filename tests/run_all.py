# -*- coding: utf-8 -*-
"""
Vector 全量回归测试入口。

用法（managed Python）:
    C:\\Users\\50350\\.workbuddy\\binaries\\python\\versions\\3.13.12\\python.exe tests\\run_all.py
        附加参数:
          --skip-integration   跳过真实 Vector.exe 集成验证
          --exe <路径>         集成层改为验证指定 exe（例如发布出来的绿色版单文件）
          --no-build           跳过编译，直接跑已存在的测试宿主与目标 exe
                               （用于清理中间产物之后，从"干净目录"复验发布包，
                                 不会重新生成 obj/ 等构建中间件）

覆盖三层:
    [1] 编译         Release 全量构建，必须 0 error
    [2] 单元/契约层  tests/Vector.RegressionTests —— 直接驱动 GestureOverlayWindow，
                     断言停车/铺屏/坐标换算/清轨迹/QUNS 循环恢复
    [3] 集成层       启动真实 Vector.exe，断言空闲时系统不处于 QUNS_BUSY
                     （这正是 Gemini 热键失效的直接原因）
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PY = sys.executable

if sys.platform == "win32":
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

user32 = ctypes.WinDLL("user32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
shell32.SHQueryUserNotificationState.argtypes = [ctypes.POINTER(ctypes.c_int)]

SM_X, SM_Y, SM_CX, SM_CY = 76, 77, 78, 79
GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000
WS_EX_TRANSPARENT = 0x00000020
QUNS_BUSY = 2

results = []   # (层, 名称, 'PASS'/'FAIL'/'SKIP', 说明)


def record(layer, name, status, detail=""):
    results.append((layer, name, status, detail))
    mark = {"PASS": "[PASS]", "FAIL": "[FAIL]", "SKIP": "[SKIP]"}[status]
    print("  %s %s%s" % (mark, name, ("  -> " + detail) if detail else ""))


def quns():
    state = ctypes.c_int(-1)
    shell32.SHQueryUserNotificationState(ctypes.byref(state))
    return state.value


def metrics():
    return (user32.GetSystemMetrics(SM_X), user32.GetSystemMetrics(SM_Y),
            user32.GetSystemMetrics(SM_CX), user32.GetSystemMetrics(SM_CY))


def proc_windows(pid):
    """返回 [(hwnd, rect, exstyle)]，仅可见顶层窗口。"""
    out = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _lparam):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            r = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            ex = user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
            out.append((hwnd, (r.left, r.top, r.right - r.left, r.bottom - r.top), ex))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


def intersects(a, b):
    return (a[0] < b[0] + b[2] and a[0] + a[2] > b[0]
            and a[1] < b[1] + b[3] and a[1] + a[3] > b[1])


def find_vector_pids():
    out = subprocess.run(["tasklist", "/FO", "CSV", "/NH", "/FI", "IMAGENAME eq Vector.exe"],
                         capture_output=True, text=True, errors="replace").stdout
    pids = []
    for line in out.splitlines():
        if line.lower().startswith('"vector.exe"'):
            parts = [x.strip('"') for x in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                pids.append(int(parts[1]))
    return pids


def kill_tree(pid):
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],
                   capture_output=True, text=True, errors="replace")


def run(cmd, cwd=ROOT):
    print("$ " + " ".join(cmd))
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, errors="replace")
    return p.returncode, (p.stdout or "") + (p.stderr or "")


# ── 层 1：编译 ──────────────────────────────────────────────────────────────
def layer_build(no_build=False):
    print()
    print("=" * 70)
    print("[层 1] Release 全量编译")
    print("=" * 70)
    if no_build:
        record("编译", "Vector.csproj Release 构建", "SKIP", "--no-build 指定跳过")
        return True
    code, out = run(["dotnet", "build", "Vector.csproj", "-c", "Release", "--nologo", "-v", "minimal"])
    tail = "\n".join(out.strip().splitlines()[-12:])
    print(tail)
    if code != 0:
        record("编译", "Vector.csproj Release 构建", "FAIL", "exit=%d" % code)
        return False
    warns = tail.count("warning")
    record("编译", "Vector.csproj Release 构建", "PASS", "warning 提及 %d 次" % warns)
    return True


# ── 层 2：回归测试套件 ──────────────────────────────────────────────────────
def layer_unit(no_build=False):
    print()
    print("=" * 70)
    print("[层 2] GestureOverlayWindow 契约回归测试")
    print("=" * 70)

    proj = os.path.join(ROOT, "tests", "Vector.RegressionTests", "Vector.RegressionTests.csproj")
    if no_build:
        record("契约", "测试工程编译", "SKIP", "--no-build 指定跳过")
    else:
        code, out = run(["dotnet", "build", proj, "-c", "Release", "--nologo", "-v", "quiet"])
        if code != 0:
            print(out[-3000:])
            record("契约", "测试工程编译", "FAIL", "exit=%d" % code)
            return False
        record("契约", "测试工程编译", "PASS")

    exe = os.path.join(ROOT, "tests", "Vector.RegressionTests", "bin", "Release",
                       "net8.0-windows", "Vector.RegressionTests.exe")
    if not os.path.isfile(exe):
        record("契约", "测试宿主可执行文件", "FAIL", "未找到 " + exe)
        return False

    p = subprocess.run([exe], cwd=ROOT, capture_output=True, text=True, errors="replace")
    body = (p.stdout or "") + (p.stderr or "")
    print(body)

    for line in body.splitlines():
        s = line.strip()
        if s.startswith("[PASS]"):
            record("契约", s[6:].strip(), "PASS")
        elif s.startswith("[FAIL]"):
            record("契约", s[6:].strip(), "FAIL")
        elif s.startswith("[SKIP]"):
            record("契约", s[6:].strip(), "SKIP")

    if p.returncode != 0:
        record("契约", "测试套件整体退出码", "FAIL", "exit=%d" % p.returncode)
        return False
    record("契约", "测试套件整体退出码", "PASS")
    return True


# ── 层 3：真实进程集成验证 ──────────────────────────────────────────────────
def layer_integration(exe_override=None):
    print()
    print("=" * 70)
    print("[层 3] 真实 Vector.exe 集成验证（空闲时不得把系统置成全屏/忙碌）")
    print("=" * 70)

    exe = exe_override or os.path.join(ROOT, "bin", "Release", "net8.0-windows", "Vector.exe")
    if not os.path.isfile(exe):
        record("集成", "构建产物 Vector.exe", "FAIL", "未找到 " + exe)
        return False
    print("  被测 exe: %s (%.1f MB)" % (exe, os.path.getsize(exe) / 1048576.0))

    if find_vector_pids():
        record("集成", "启动前无 Vector.exe 在运行", "FAIL", "请先退出已运行的 Vector")
        return False
    record("集成", "启动前无 Vector.exe 在运行", "PASS")

    data_dir = os.path.join(os.environ.get("APPDATA", ""), "Vector")
    cfg = os.path.join(data_dir, "config.json")
    if os.path.isfile(cfg):
        try:
            with open(cfg, encoding="utf-8-sig") as fh:
                if json.load(fh).get("RunAsAdmin"):
                    record("集成", "配置未开启 RunAsAdmin", "SKIP", "配置要求提权，跳过自动启动")
                    return True
        except Exception:
            pass
    record("集成", "配置未开启 RunAsAdmin", "PASS")

    virt = metrics()
    base = quns()
    print("  启动前 SHQueryUserNotificationState = %d  (虚拟桌面 %s)" % (base, virt))

    proc = subprocess.Popen([exe], cwd=os.path.dirname(exe))
    try:
        time.sleep(6.0)

        samples = []
        for _ in range(4):
            samples.append(quns())
            time.sleep(0.5)
        print("  空闲 6s 后连续采样 QUNS = %s" % samples)

        wins = proc_windows(proc.pid)
        print("  Vector.exe 可见顶层窗口 %d 个：" % len(wins))
        overlay_like = []
        for hwnd, rect, ex in wins:
            flags = []
            if ex & WS_EX_LAYERED:
                flags.append("LAYERED")
            if ex & WS_EX_NOACTIVATE:
                flags.append("NOACTIVATE")
            if ex & WS_EX_TRANSPARENT:
                flags.append("TRANSPARENT")
            print("    hwnd=0x%08X rect=%s ex=[%s]" % (hwnd, rect, ",".join(flags)))
            if ex & WS_EX_LAYERED:
                overlay_like.append((hwnd, rect))

        if base != QUNS_BUSY:
            ok = all(s == base for s in samples)
            record("集成", "空闲态系统状态未被置成 BUSY",
                   "PASS" if ok else "FAIL",
                   "采样=%s 基线=%d" % (samples, base))
        else:
            record("集成", "空闲态系统状态未被置成 BUSY", "SKIP", "启动前基线已是 BUSY")

        bad = [(h, r) for h, r in overlay_like if intersects(r, virt)]
        record("集成", "遮罩窗未覆盖任何显示器",
               "PASS" if not bad else "FAIL",
               "" if not bad else "覆盖中的窗口: " + repr(bad))
    finally:
        kill_tree(proc.pid)
        time.sleep(1.0)
        record("集成", "测试后已回收 Vector.exe 进程",
               "PASS" if not find_vector_pids() else "FAIL")

    return True


def main():
    args = sys.argv[1:]
    no_build = "--no-build" in args
    exe_override = None
    if "--exe" in args:
        idx = args.index("--exe")
        if idx + 1 >= len(args):
            print("--exe 缺少路径参数")
            return 2
        exe_override = args[idx + 1]

    print("Vector 全量回归测试")
    print("项目目录: " + ROOT)
    print("Python  : " + PY)
    if no_build:
        print("模式    : --no-build（跳过编译，复验现有产物）")

    ok = layer_build(no_build)
    if ok:
        ok = layer_unit(no_build)
    else:
        record("契约", "回归测试套件", "SKIP", "编译失败，未执行")

    if "--skip-integration" in args:
        record("集成", "真实进程集成验证", "SKIP", "命令行要求跳过")
    else:
        layer_integration(exe_override)

    print()
    print("=" * 70)
    print("汇总")
    print("=" * 70)
    counts = {"PASS": 0, "FAIL": 0, "SKIP": 0}
    for layer, name, status, detail in results:
        counts[status] += 1
    for layer in ("编译", "契约", "集成"):
        rows = [r for r in results if r[0] == layer]
        if not rows:
            continue
        print("  %s：PASS %d / FAIL %d / SKIP %d" % (
            layer,
            sum(1 for r in rows if r[2] == "PASS"),
            sum(1 for r in rows if r[2] == "FAIL"),
            sum(1 for r in rows if r[2] == "SKIP")))
    print("  " + "-" * 50)
    print("  总计：PASS %d / FAIL %d / SKIP %d" % (counts["PASS"], counts["FAIL"], counts["SKIP"]))
    print("  结论：" + ("全部通过" if counts["FAIL"] == 0 else "存在失败"))
    return 0 if counts["FAIL"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
