"""渲染和独立审计共用的离线 HTML5 依赖；不包含渲染或审计算法。"""

from hashlib import sha256
from pathlib import Path
import sys
import zipfile


HTML5_PROFILE = {"version":"selectolax 1.0.2 / Lexbor (MIT)",
    "distribution":"https://pypi.org/project/selectolax/1.0.2/#files",
    "wheel":"selectolax-1.0.2-cp314-cp314-win_amd64.whl","wheel_sha256":"98bff51f30126bc1640c596892e27dfeacfbeac470e9cf717894654404e953b6",
    "pyd_sha256":"d6a130eb85da3944ab0f3ce4581718c3a9325c68850ed779357b1298d807ebc3"}


def load_html5_parser():
    """每次校验固定 wheel 和原生模块；是否缓存由调用方决定。"""
    base = Path(__file__).resolve().parent / ".tools"
    wheel, unpacked = base / HTML5_PROFILE["wheel"], base / "selectolax-1.0.2"
    if not wheel.is_file() or sha256(wheel.read_bytes()).hexdigest() != HTML5_PROFILE["wheel_sha256"]:
        raise ValueError(f"请从 {HTML5_PROFILE['distribution']} 恢复 {wheel} 并核对 SHA-256")
    pyd = unpacked / "selectolax/lexbor.cp314-win_amd64.pyd"
    if not pyd.is_file():
        with zipfile.ZipFile(wheel) as archive:
            archive.extractall(unpacked)
    if sha256(pyd.read_bytes()).hexdigest() != HTML5_PROFILE["pyd_sha256"]:
        raise ValueError("HTML5 原生解析器校验失败")
    sys.path.insert(0, str(unpacked))
    from selectolax.lexbor import LexborHTMLParser
    return LexborHTMLParser
