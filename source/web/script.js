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

class Slideshow {
    constructor() {
        this.files = [];
        this.currentIndex = 0;
        this.timer = null;
        this.cleanupTimer = null;
        this.currentContainer = -1;
        this.slideToken = 0;
        this.advancing = false;
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

    applyConfigStyles() {
        const fadeSeconds = Math.max(0, Number(this.config.fadeTransitionDuration ?? 1) || 0);
        document.documentElement.style.setProperty('--fade-duration', `${fadeSeconds}s`);
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
                if (this.currentIndex >= this.files.length) {
                    this.currentIndex = 0;
                }
                if (preserveCurrentPass && wasEmpty && !this.advancing) {
                    this.currentIndex = 0;
                    this.showCurrentSlide();
                }
                return;
            }

            const currentName = this.files[this.currentIndex]?.name;
            const isRandom = String(this.config.displayOrder || '').toLowerCase() === 'random';

            if (isRandom) {
                // Keep the in-progress pass order while dropping deleted files and appending new ones.
                const incomingByName = new Map(incoming.map(file => [file.name, file]));
                const merged = this.files.filter(file => incomingByName.has(file.name));
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

            if (currentName !== undefined) {
                const updatedIndex = this.files.findIndex(file => file.name === currentName);
                this.currentIndex = updatedIndex >= 0
                    ? updatedIndex
                    : Math.min(this.currentIndex, Math.max(0, this.files.length - 1));
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
            if (this.files.length === 0) return;
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
        const token = ++this.slideToken;
        const currentFile = this.files[this.currentIndex];
        const nextContainer = this.currentContainer === 0 ? 1 : 0;
        const containerElement = document.getElementById(`mediaContainer${nextContainer + 1}`);

        this.teardownContainer(containerElement);

        const mediaUrl = `media/${encodeURIComponent(currentFile.name)}`;
        let mediaElement;

        if (currentFile.type === 'video') {
            mediaElement = document.createElement('video');
            mediaElement.src = mediaUrl;
            mediaElement.autoplay = true;
            mediaElement.muted = true;
            mediaElement.controls = false;
            mediaElement.preload = 'auto';
            mediaElement.playsInline = true;
            mediaElement.setAttribute('playsinline', '');

            let lastPlaybackTime = -1;

            mediaElement.addEventListener('loadedmetadata', () => {
                if (token !== this.slideToken) return;
                this.onSlideAdvanced();
                this.switchToContainer(nextContainer);
                this.playVideo(mediaElement);
            });

            mediaElement.addEventListener('canplay', () => {
                if (token !== this.slideToken) return;
                if (mediaElement.paused) {
                    this.playVideo(mediaElement);
                }
            });

            // Refresh watchdog timestamp only while playback position advances so stalled videos still recover.
            mediaElement.addEventListener('timeupdate', () => {
                if (token !== this.slideToken) return;
                if (!mediaElement.paused && !mediaElement.ended && mediaElement.currentTime !== lastPlaybackTime) {
                    lastPlaybackTime = mediaElement.currentTime;
                    this.lastAdvanceAt = Date.now();
                }
            });

            mediaElement.addEventListener('ended', () => {
                if (token !== this.slideToken) return;
                this.nextSlide().then(() => {});
            });

            mediaElement.addEventListener('error', e => {
                if (token !== this.slideToken) return;
                console.error('Video error:', e);
                this.handleMediaError();
                this.nextSlide().then(() => {});
            });
        } else {
            mediaElement = document.createElement('img');
            mediaElement.src = mediaUrl;

            if (this.config.zoomOnImage) {
                mediaElement.style.setProperty('--zoom-duration', `${this.config.imageDuration}s`);
                mediaElement.classList.add(this.currentIndex % 2 === 0 ? 'zoom-in' : 'zoom-out');
            }

            mediaElement.addEventListener('load', () => {
                if (token !== this.slideToken) return;
                this.onSlideAdvanced();
                this.switchToContainer(nextContainer);
                this.scheduleNext(this.config.imageDuration * 1000, token);
            });

            mediaElement.addEventListener('error', e => {
                if (token !== this.slideToken) return;
                console.error('Image error:', e);
                this.handleMediaError();
                this.nextSlide().then(() => {});
            });
        }

        containerElement.appendChild(mediaElement);
    }

    async playVideo(videoElement) {
        try {
            await videoElement.play();
        } catch (error) {
            if (error.name !== 'AbortError') {
                console.error('Failed to autoplay video:', error);
            }
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

            if (this.cleanupTimer) {
                clearTimeout(this.cleanupTimer);
            }
            const fadeMs = Math.max(0, Number(this.config.fadeTransitionDuration ?? 1) || 0) * 1000;
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

    async nextSlide() {
        if (this.advancing) return;
        this.advancing = true;
        this.clearTimer();

        try {
            this.currentIndex++;

            if (this.currentIndex >= this.files.length) {
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

    handleMediaError() {
        this.consecutiveMediaErrors++;
        if (this.consecutiveMediaErrors >= MAX_CONSECUTIVE_MEDIA_ERRORS) {
            console.warn(`Reloading page after ${this.consecutiveMediaErrors} consecutive media errors`);
            window.location.reload();
        }
    }
}

document.addEventListener('DOMContentLoaded', () => {
    new Slideshow();
});
