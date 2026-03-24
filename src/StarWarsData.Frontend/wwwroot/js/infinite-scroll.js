const observers = new Map();

export function observe(sentinel, dotNetRef) {
    dispose(sentinel);
    const observer = new IntersectionObserver(entries => {
        if (entries[0].isIntersecting) {
            dotNetRef.invokeMethodAsync('OnSentinelVisible');
        }
    }, { rootMargin: '400px' });
    observer.observe(sentinel);
    observers.set(sentinel, observer);
}

export function dispose(sentinel) {
    const observer = observers.get(sentinel);
    if (observer) {
        observer.disconnect();
        observers.delete(sentinel);
    }
}
