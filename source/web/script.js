const DEFAULT_CONFIG = {
    imageDuration: 5,
    fadeTransitionDuration: 1,
    zoomOnImage: true,
    displayOrder: 'alpha',
};

const FILE_LIST_REFRESH_MS = 5 * 60 * 1000;
const FETCH_TIMEOUT_MS = 10 * 1000;
const RETRY_BACKOFF_MS = [2000, 5000, 10000, 30000, 60000];
const WATCHDOG_MULTIPLIER = 4;
const WATCHDOG_MIN_MS = 20 * 1000;
const MAX_CONSECUTIVE_MEDIA_ERRORS = 25;

async function fetchWithTimeout(url, options, timeoutMs) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
        const response = await fetch(url, { ...options, signal: controller.signal, cache: 'no-store' });
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }
        return response;
    } finally {
        clearTimeout(timer);
    }
}

async function fetchJsonWithTimeout(url, timeoutMs) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
        const response = await fetch(url, { signal: controller.signal, cache: 'no-store' });
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }
        return await response.json();
    } finally {
        clearTimeout(timer);
    }
}

function backoffFor(attempt) {
    const index = Math.min(attempt, RETRY_BACKOFF_MS.length - 1);
    return RETRY_BACKOFF_MS[index];
}

function mediaErrorDelayFor(errorCount) {
    if (errorCount <= 2) return 0;
    if (errorCount <= 5) return 1000;
    if (errorCount <= 10) return 2500;
    return 5000;
}

class Slideshow {
    constructor() {
        this.files = [];
        this.currentIndex = 0;
        this.timer = null;
        this.cleanupTimer = null;
        this.currentContainer = -1;
        this.slideToken = 0;
        this.advancing = false;
        this.recovering = false;
        this.refreshInFlight = null;
        this.config = { ...DEFAULT_CONFIG };
        this.lastAdvanceAt = Date.now();
        this.consecutiveMediaErrors = 0;
        this.fileRefreshTimer = null;
        this.watchdogTimer = null;

        this.applyConfigStyles();
        this.init().then(() => {});
    }

    async init() {
        await this.loadConfigWithRetry();
        await this.loadFilesUntilNonEmpty();
        this.hideLoading();
        this.hideWaiting();
        this.startSlideshow();
        this.startFileRefresh();
        this.startWatchdog();
    }

    fadeDurationSeconds() {
        return Math.max(0, Number(this.config.fadeTransitionDuration ?? 1) || 0);
    }

    applyConfigStyles() {
        document.documentElement.style.setProperty('--fade-duration', `${this.fadeDurationSeconds()}s`);
    }

    async loadConfigWithRetry() {
        let attempt = 0;
        while (true) {
            try {
                const cfg = await fetchJsonWithTimeout('/api/config', FETCH_TIMEOUT_MS);
                this.config = { ...DEFAULT_CONFIG, ...cfg };
                this.applyConfigStyles();
                return;
            } catch (error) {
                const wait = backoffFor(attempt++);
                console.warn(`Config load failed (${error.message}); retrying in ${wait}ms`);
                this.showWaiting('Waiting for server (config)...');
                await new Promise(resolve => setTimeout(resolve, wait));
            }
        }
    }

    async loadFilesUntilNonEmpty() {
        let attempt = 0;
        while (true) {
            try {
                const files = await fetchJsonWithTimeout('/api/files', FETCH_TIMEOUT_MS);
                if (Array.isArray(files) && files.length > 0) {
                    this.files = files;
                    return;
                }
                this.showWaiting('No media files available yet...');
            } catch (error) {
                console.warn(`Files load failed (${error.message})`);
                this.showWaiting('Waiting for server (files)...');
            }
            const wait = backoffFor(attempt++);
            await new Promise(resolve => setTimeout(resolve, wait));
        }
    }

    async refreshFilesQuiet(preserveCurrentPass = false) {
        if (this.refreshInFlight) {
            if (preserveCurrentPass) {
                return this.refreshInFlight;
            }
            await this.refreshInFlight;
        }

        const task = this.performFileRefresh(preserveCurrentPass);
        this.refreshInFlight = task;
        try {
            await task;
        } finally {
            if (this.refreshInFlight === task) {
                this.refreshInFlight = null;
            }
        }
    }

    async performFileRefresh(preserveCurrentPass) {
        try {
            const incoming = await fetchJsonWithTimeout('/api/files', FETCH_TIMEOUT_MS);
            if (!Array.isArray(incoming)) {
                return;
            }

            if (incoming.length === 0) {
                const hadFiles = this.files.length > 0;
                this.files = [];
                this.currentIndex = 0;
                if (hadFiles) {
                    this.slideToken++;
                    this.clearTimer();
                    this.teardownAllContainers();
                    this.showWaiting('No media files available yet...');
                    this.scheduleEmptyPoll();
                }
                return;
            }

            if (!preserveCurrentPass || this.files.length === 0) {
                const wasEmpty = this.files.length === 0;
                this.files = incoming;
                if (this.currentIndex >= this.files.length || this.currentIndex < 0) {
                    this.currentIndex = 0;
                }
                if (preserveCurrentPass && wasEmpty && !this.advancing && !this.recovering) {
                    this.currentIndex = 0;
                    this.showCurrentSlide();
                }
                return;
            }

            const previousList = this.files;
            const previousIndex = this.currentIndex;
            const currentName = previousIndex >= 0 ? previousList[previousIndex]?.name : undefined;
            const isRandom = String(this.config.displayOrder || '').toLowerCase() === 'random';

            if (isRandom) {
                // Keep the in-progress pass order while picking up updated versions and appending new files.
                const incomingByName = new Map(incoming.map(file => [file.name, file]));
                const merged = [];
                for (const file of previousList) {
                    const refreshed = incomingByName.get(file.name);
                    if (refreshed) {
                        merged.push(refreshed);
                    }
                }
                const existingNames = new Set(merged.map(file => file.name));
                for (const file of incoming) {
                    if (!existingNames.has(file.name)) {
                        merged.push(file);
                    }
                }
                this.files = merged;
            } else {
                this.files = incoming;
            }

            if (previousIndex < 0) {
                this.currentIndex = -1;
                return;
            }

            if (currentName !== undefined) {
                const updatedIndex = this.files.findIndex(file => file.name === currentName);
                if (updatedIndex >= 0) {
                    this.currentIndex = updatedIndex;
                    return;
                }

                // Step back one slot before the next surviving slide so nextSlide()'s increment lands on it.
                let nextSurvivingIndex = -1;
                for (let i = previousIndex + 1; i < previousList.length; i++) {
                    const idx = this.files.findIndex(file => file.name === previousList[i].name);
                    if (idx >= 0) {
                        nextSurvivingIndex = idx;
                        break;
                    }
                }
                this.currentIndex = nextSurvivingIndex >= 0 ? nextSurvivingIndex - 1 : this.files.length - 1;
            }
        } catch (error) {
            console.warn(`File refresh skipped (${error.message})`);
        }
    }

    startFileRefresh() {
        if (this.fileRefreshTimer) clearInterval(this.fileRefreshTimer);
        this.fileRefreshTimer = setInterval(() => {
            this.refreshFilesQuiet(true);
        }, FILE_LIST_REFRESH_MS);
    }

    startWatchdog() {
        if (this.watchdogTimer) clearInterval(this.watchdogTimer);
        this.watchdogTimer = setInterval(() => {
            if (this.files.length === 0 || this.recovering) return;
            const maxIdle = Math.max(
                WATCHDOG_MIN_MS,
                (this.config.imageDuration || 5) * 1000 * WATCHDOG_MULTIPLIER,
            );
            if (Date.now() - this.lastAdvanceAt > maxIdle) {
                console.warn('Watchdog: no slide progress detected; advancing slide');
                this.lastAdvanceAt = Date.now();
                this.nextSlide().then(() => {});
            }
        }, 5000);
    }

    hideLoading() {
        const loading = document.querySelector('.loading');
        if (loading) {
            loading.remove();
        }
    }

    showWaiting(message) {
        this.hideLoading();
        this.hideWaiting();
        const waiting = document.createElement('div');
        waiting.className = 'waiting';
        waiting.textContent = message;
        document.body.appendChild(waiting);
    }

    hideWaiting() {
        const waiting = document.querySelector('.waiting');
        if (waiting) {
            waiting.remove();
        }
    }

    startSlideshow() {
        if (this.files.length === 0) return;

        this.currentIndex = 0;
        this.showCurrentSlide();
    }

    showCurrentSlide() {
        if (this.files.length === 0) return;

        this.clearTimer();
        this.clearCleanupTimer();
        const token = ++this.slideToken;
        const slideIndex = this.currentIndex;
        const currentFile = this.files[slideIndex];
        const nextContainer = this.currentContainer === 0 ? 1 : 0;
        const containerElement = document.getElementById(`mediaContainer${nextContainer + 1}`);

        this.teardownContainer(containerElement);

        const versionQuery = currentFile.version ? `?v=${encodeURIComponent(currentFile.version)}` : '';
        const mediaUrl = `media/${encodeURIComponent(currentFile.name)}${versionQuery}`;

        if (currentFile.type === 'video') {
            const videoElement = document.createElement('video');
            videoElement.autoplay = true;
            videoElement.muted = true;
            videoElement.defaultMuted = true;
            videoElement.controls = false;
            videoElement.preload = 'auto';
            videoElement.playsInline = true;
            videoElement.setAttribute('muted', '');
            videoElement.setAttribute('playsinline', '');

            let lastPlaybackTime = -1;
            let revealed = false;

            const revealVideo = () => {
                if (revealed || token !== this.slideToken) return;
                revealed = true;
                this.onSlideAdvanced();
                this.switchToContainer(nextContainer);
            };

            videoElement.addEventListener('loadedmetadata', () => {
                if (token !== this.slideToken) return;
                this.playVideo(videoElement, token);
            });

            videoElement.addEventListener('loadeddata', revealVideo);
            videoElement.addEventListener('playing', revealVideo);

            videoElement.addEventListener('canplay', () => {
                if (token !== this.slideToken) return;
                if (videoElement.paused) {
                    this.playVideo(videoElement, token);
                }
            });

            // Refresh watchdog timestamp only while playback position advances so stalled videos still recover.
            videoElement.addEventListener('timeupdate', () => {
                if (token !== this.slideToken) return;
                if (!videoElement.paused && !videoElement.ended && videoElement.currentTime !== lastPlaybackTime) {
                    lastPlaybackTime = videoElement.currentTime;
                    this.lastAdvanceAt = Date.now();
                }
            });

            videoElement.addEventListener('ended', () => {
                if (token !== this.slideToken) return;
                this.nextSlide().then(() => {});
            });

            videoElement.addEventListener('error', e => {
                if (token !== this.slideToken) return;
                console.error('Video error:', e);
                this.onMediaError(token);
            });

            videoElement.src = mediaUrl;
            containerElement.appendChild(videoElement);
        } else {
            const imageElement = document.createElement('img');
            const zoomClass = slideIndex % 2 === 0 ? 'zoom-in' : 'zoom-out';

            imageElement.addEventListener('load', async () => {
                if (token !== this.slideToken) return;

                if (typeof imageElement.decode === 'function') {
                    try {
                        await imageElement.decode();
                    } catch {
                        if (!imageElement.complete || imageElement.naturalWidth === 0) {
                            if (token === this.slideToken) {
                                this.onMediaError(token);
                            }
                            return;
                        }
                    }
                    if (token !== this.slideToken) return;
                }

                this.onSlideAdvanced();
                if (this.config.zoomOnImage) {
                    const durationSeconds = (this.config.imageDuration || 5) + this.fadeDurationSeconds();
                    imageElement.style.setProperty('--zoom-duration', `${durationSeconds}s`);
                    imageElement.classList.add(zoomClass);
                }
                this.switchToContainer(nextContainer);
                this.scheduleNext((this.config.imageDuration || 5) * 1000, token);
            });

            imageElement.addEventListener('error', e => {
                if (token !== this.slideToken) return;
                console.error('Image error:', e);
                this.onMediaError(token);
            });

            imageElement.src = mediaUrl;
            containerElement.appendChild(imageElement);
        }
    }

    async playVideo(videoElement, token) {
        try {
            await videoElement.play();
        } catch (error) {
            if (token !== this.slideToken || error.name === 'AbortError') {
                return;
            }
            console.error('Failed to autoplay video:', error);
            this.onMediaError(token);
        }
    }

    switchToContainer(containerIndex) {
        const previousIndex = this.currentContainer;

        document.querySelectorAll('.media-container').forEach(container => {
            container.classList.remove('active');
        });

        document.getElementById(`mediaContainer${containerIndex + 1}`).classList.add('active');
        this.currentContainer = containerIndex;

        if (previousIndex >= 0 && previousIndex !== containerIndex) {
            const outgoing = document.getElementById(`mediaContainer${previousIndex + 1}`);
            const outgoingVideo = outgoing.querySelector('video');
            if (outgoingVideo) {
                outgoingVideo.pause();
            }

            this.clearCleanupTimer();
            const fadeMs = this.fadeDurationSeconds() * 1000;
            this.cleanupTimer = setTimeout(() => {
                this.cleanupTimer = null;
                if (this.currentContainer !== previousIndex) {
                    this.teardownContainer(outgoing);
                }
            }, fadeMs + 100);
        }
    }

    teardownContainer(container) {
        if (!container) return;
        const videos = container.querySelectorAll('video');
        videos.forEach(video => {
            video.pause();
            video.removeAttribute('src');
            video.load();
        });
        container.innerHTML = '';
    }

    teardownAllContainers() {
        this.clearCleanupTimer();
        document.querySelectorAll('.media-container').forEach(container => {
            container.classList.remove('active');
            this.teardownContainer(container);
        });
        this.currentContainer = -1;
    }

    scheduleNext(duration, token) {
        this.clearTimer();
        this.timer = setTimeout(() => {
            if (token !== this.slideToken) return;
            this.nextSlide().then(() => {});
        }, duration);
    }

    scheduleEmptyPoll() {
        this.clearTimer();
        const token = this.slideToken;
        this.timer = setTimeout(() => {
            if (token !== this.slideToken) return;
            this.nextSlide().then(() => {});
        }, 5000);
    }

    clearTimer() {
        if (this.timer) {
            clearTimeout(this.timer);
            this.timer = null;
        }
    }

    clearCleanupTimer() {
        if (this.cleanupTimer) {
            clearTimeout(this.cleanupTimer);
            this.cleanupTimer = null;
        }
    }

    async nextSlide() {
        if (this.advancing || this.recovering) return;
        this.advancing = true;
        this.clearTimer();

        try {
            this.currentIndex++;

            if (this.currentIndex >= this.files.length || this.currentIndex < 0) {
                await this.refreshFilesQuiet(false);
                this.currentIndex = 0;
            }

            if (this.files.length > 0) {
                this.showCurrentSlide();
            } else {
                this.showWaiting('No media files available yet...');
                this.scheduleEmptyPoll();
            }
        } finally {
            this.advancing = false;
        }
    }

    onSlideAdvanced() {
        this.lastAdvanceAt = Date.now();
        this.consecutiveMediaErrors = 0;
        this.hideWaiting();
    }

    onMediaError(token) {
        this.consecutiveMediaErrors++;
        if (this.consecutiveMediaErrors >= MAX_CONSECUTIVE_MEDIA_ERRORS) {
            this.recoverFromMediaErrors(token).then(() => {});
            return;
        }

        const delayMs = mediaErrorDelayFor(this.consecutiveMediaErrors);
        this.lastAdvanceAt = Date.now();
        this.scheduleNext(delayMs, token);
    }

    async recoverFromMediaErrors(token) {
        if (this.recovering || token !== this.slideToken) return;
        this.recovering = true;
        this.clearTimer();

        try {
            // Probe the server before reloading so a restart never strands the browser on an error page.
            try {
                await fetchWithTimeout('/', { method: 'HEAD' }, 5000);
                console.warn(`Server reachable after ${this.consecutiveMediaErrors} media errors; reloading page`);
                window.location.reload();
                return;
            } catch {
                console.warn('Server unreachable after media errors; keeping last slide until server responds');
            }

            let attempt = 0;
            while (token === this.slideToken) {
                this.lastAdvanceAt = Date.now();
                const wait = backoffFor(attempt++);
                await new Promise(resolve => setTimeout(resolve, wait));
                if (token !== this.slideToken) return;

                try {
                    const cfg = await fetchJsonWithTimeout('/api/config', FETCH_TIMEOUT_MS);
                    this.config = { ...DEFAULT_CONFIG, ...cfg };
                    this.applyConfigStyles();
                    await this.refreshFilesQuiet(false);
                    this.consecutiveMediaErrors = 0;
                    this.lastAdvanceAt = Date.now();
                    break;
                } catch {
                    // Server is still restarting; stay on the currently displayed slide.
                }
            }
        } finally {
            this.recovering = false;
        }

        if (token === this.slideToken) {
            await this.nextSlide();
        }
    }
}

document.addEventListener('DOMContentLoaded', () => {
    new Slideshow();
});
