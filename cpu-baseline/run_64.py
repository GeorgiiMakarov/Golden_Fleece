"""64^3 driver: re-execs simulate.py with N=64 for the solver-verdict experiment.

Usage: FLUID_DUMP_STEPS="8,60,120,200" FLUID_TAG_SUFFIX="n64" python3 run_64.py <argv...>
Writes output-<tag>-n64/ with frames, checks.json and state_stepNNNN.npz dumps.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, "simulate.py")).read()
assert "\nN = 32\n" in src, "N = 32 line not found"
src = src.replace("\nN = 32\n", "\nN = 64\n", 1)
src = src.split('if __name__ == "__main__":')[0]
g = {"__name__": "notmain64", "__file__": os.path.join(HERE, "simulate.py"),
     "sys": sys}
exec(src, g)

scheme = sys.argv[1] if len(sys.argv) > 1 else "mc"
vort_eps = float(sys.argv[2]) if len(sys.argv) > 2 else 0.15
buoy_beta = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0
steps = int(sys.argv[4]) if len(sys.argv) > 4 else 240
solver = sys.argv[5] if len(sys.argv) > 5 else "jacobi"
iters = int(sys.argv[6]) if len(sys.argv) > 6 else 50
chi_mode = sys.argv[7] if len(sys.argv) > 7 else "sweep"
g["main"](scheme, vort_eps, buoy_beta, steps, solver, iters, chi_mode)
