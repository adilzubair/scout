"""Terminate an owned development process tree; production ownership uses Windows jobs."""
def stop_tree(process):
    import psutil
    try:
        children = psutil.Process(process.pid).children(recursive=True)
    except psutil.NoSuchProcess:
        children = []
    for child in reversed(children):
        try: child.kill()
        except psutil.NoSuchProcess: pass
    if process.poll() is None: process.kill()
    process.wait(timeout=10)
    psutil.wait_procs(children,timeout=10)
