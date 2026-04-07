const STORAGE_KEY = "starwars-chat-sessions";

export function loadSessions() {
    try {
        const data = localStorage.getItem(STORAGE_KEY);
        return data ? JSON.parse(data) : [];
    } catch {
        return [];
    }
}

export function saveSession(session) {
    const sessions = loadSessions();
    const idx = sessions.findIndex(s => s.id === session.id);
    if (idx >= 0) {
        sessions[idx] = session;
    } else {
        sessions.unshift(session);
    }
    localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
}

export function loadSession(sessionId) {
    const sessions = loadSessions();
    return sessions.find(s => s.id === sessionId) || null;
}

export function deleteSession(sessionId) {
    const sessions = loadSessions().filter(s => s.id !== sessionId);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
}

export function clearAll() {
    localStorage.removeItem(STORAGE_KEY);
}
