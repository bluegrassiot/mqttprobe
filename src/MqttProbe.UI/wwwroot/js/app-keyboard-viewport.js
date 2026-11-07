// Keeps the app shell out of the way of an iOS keyboard. The native view reports
// whether a keyboard is present; this script owns the classes and the geometry the CSS reads.
(() => {
    const KEYBOARD_CLASS = 'app-keyboard-open';
    const EDITOR_IN_HEADER_CLASS = 'app-keyboard-editor-in-header';
    const HEIGHT_VARIABLE = '--app-keyboard-height';
    const TOP_INSET_VARIABLE = '--app-keyboard-top-inset';
    const NATIVE_STATE_ATTRIBUTE = 'data-app-native-keyboard-state';
    const RECOVERY_ATTRIBUTE = 'data-app-keyboard-viewport-recovery';
    const NATIVE_CHANGE_EVENT = 'app-native-keyboard-change';
    const VIEWPORT_CHANGE_EVENT = 'app-keyboard-viewport-change';
    const REVEAL_MARGIN = 8;
    const MIN_KEYBOARD_OCCLUSION = 120;
    const BAND_TOLERANCE = 1;
    const SCROLL_RESET_TOLERANCE = 2;
    const SCROLL_SETTLE_MS = 120;
    const MIN_REST_HEIGHT_DROP = MIN_KEYBOARD_OCCLUSION;
    // Only coarse pointers get a native view that can shrink for a keyboard.
    const coarsePointer = window.matchMedia('(pointer: coarse)');
    let lastWidth = window.innerWidth;
    let idleHeight = window.innerHeight;
    let frameHandle = 0;
    let scrollSettleAt = 0;
    let scrollCorrectionUsed = false;
    let postureTimer = 0;
    let posturePending = false;
    let postureGeneration = 0;
    let postureSettledWidth = window.innerWidth;
    let correctedWidth = null;
    let recoveryHoldUntil = 0;
    let restRoot = null;
    let keyboardEpisode = false;
    let episodeRestHeight = 0;
    let episodeWidth = 0;
    let restorationWitness = null;
    let lastBand = null;
    let recoveryBand = null;
    let recoveryRestHeight = 0;
    let recoveryWidth = 0;
    let recoveryViewportHeight = 0;
    let dismissedByNative = false;
    let publishedSignature = '';

    const isPositive = (value) => typeof value === 'number' && isFinite(value) && value > 0;

    // Raw scale is the guard's input; the normalized scale stays for occlusion math.
    const rawScaleIsOne = () => {
        const raw = window.visualViewport ? window.visualViewport.scale : 1;
        return typeof raw === 'number' && isFinite(raw) && Math.abs(raw - 1) <= 0.01;
    };

    // A whole-document pan leaves window.innerHeight shorter than the layout box the
    // native container and the CSS frame still use.
    const layoutReference = (layoutHeight, recovering) => {
        const docHeight = document.documentElement.clientHeight;
        return recovering && isPositive(docHeight) && docHeight > layoutHeight + BAND_TOLERANCE
            ? docHeight
            : layoutHeight;
    };

    // Absent is a plain browser; unknown is a native frame that cannot answer yet.
    const nativeState = () => {
        const value = document.documentElement.getAttribute(NATIVE_STATE_ATTRIBUTE);
        return value === 'open' || value === 'closed' || value === 'unknown' ? value : 'absent';
    };

    const isEditing = () => {
        const active = document.activeElement;
        return active instanceof HTMLElement
            && active.matches('input, textarea, select, [contenteditable="true"]');
    };

    const removeGeometry = () => {
        document.documentElement.style.removeProperty(HEIGHT_VARIABLE);
        document.documentElement.style.removeProperty(TOP_INSET_VARIABLE);
    };

    const revealFocusedEditor = (band) => {
        const active = document.activeElement;
        if (!band || !(active instanceof HTMLElement) || !isEditing() || active.closest('.mud-dialog-container')) {
            return;
        }

        const viewportTop = band.top;
        const viewportBottom = band.top + band.height;
        let scroller = active.parentElement;
        while (scroller && scroller !== document.body) {
            const style = getComputedStyle(scroller);
            if ((style.overflowY === 'auto' || style.overflowY === 'scroll')
                && scroller.scrollHeight > scroller.clientHeight + 1) {
                break;
            }
            scroller = scroller.parentElement;
        }

        if (!scroller || scroller === document.body) {
            active.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            return;
        }

        // A panned viewport hides both edges of the scroller band.
        const field = active.getBoundingClientRect();
        const scrollerBand = scroller.getBoundingClientRect();
        const visibleTop = Math.max(scrollerBand.top, viewportTop) + REVEAL_MARGIN;
        const visibleBottom = Math.min(scrollerBand.bottom, viewportBottom) - REVEAL_MARGIN;
        const visibleHeight = visibleBottom - visibleTop;
        // A field taller than the band cannot satisfy both edges; pin one so repeat reveals agree.
        if (visibleHeight > 0 && field.height > visibleHeight) {
            scroller.scrollTop += field.top - visibleTop;
        } else if (field.bottom > visibleBottom) {
            scroller.scrollTop += field.bottom - visibleBottom;
        } else if (field.top < visibleTop) {
            scroller.scrollTop -= visibleTop - field.top;
        }
    };

    // Only the connection dialog has the native inset pan this corrects.
    const ownsFocusedEditor = () => {
        const active = document.activeElement;
        return active instanceof HTMLElement
            && isEditing()
            && active.closest('.connection-dialog-content') !== null;
    };

    // Only an unpanned, fitting root is a trustworthy resting baseline.
    const rememberRestRoot = () => {
        const root = document.documentElement;
        // A shrink in progress must not replace the last resting baseline.
        if (window.innerHeight < idleHeight - SCROLL_RESET_TOLERANCE) {
            return;
        }

        const rested = root.scrollHeight <= root.clientHeight + SCROLL_RESET_TOLERANCE
            && window.scrollY <= SCROLL_RESET_TOLERANCE;
        restRoot = rested
            ? { width: window.innerWidth, clientHeight: root.clientHeight }
            : null;
    };

    // The root fits now, or it fitted before the keyboard took that height.
    const rootAllowsReset = () => {
        const root = document.documentElement;
        if (root.scrollHeight <= root.clientHeight + SCROLL_RESET_TOLERANCE) {
            return true;
        }

        if (!restRoot
            || Math.abs(window.innerWidth - restRoot.width) > 1
            || restRoot.clientHeight - root.clientHeight <= MIN_REST_HEIGHT_DROP) {
            return false;
        }

        return root.scrollHeight <= restRoot.clientHeight + SCROLL_RESET_TOLERANCE;
    };

    const isRootPanned = () => window.scrollY > SCROLL_RESET_TOLERANCE && rootAllowsReset();

    // Recovery is limited to a panned, fitting document with a focused dialog editor at
    // a real (unzoomed) viewport; the old width baseline cannot vouch for this posture.
    const strictPanContext = () => {
        const root = document.documentElement;
        return coarsePointer.matches
            && rawScaleIsOne()
            && (keyboardEpisode || recoveryBand !== null)
            && ownsFocusedEditor()
            && isPositive(root.clientHeight)
            && root.scrollHeight <= root.clientHeight + SCROLL_RESET_TOLERANCE
            && window.scrollY > SCROLL_RESET_TOLERANCE;
    };

    // A fresh budget still waits for the posture to settle; a width burst never corrects.
    const canCorrectPannedRoot = () => !posturePending && strictPanContext();

    let recoveryWatchdog = 0;
    let recoveryGeneration = 0;

    const cancelRecovery = () => {
        window.clearTimeout(recoveryWatchdog);
        recoveryWatchdog = 0;
        recoveryGeneration += 1;
        recoveryHoldUntil = 0;
        document.documentElement.removeAttribute(RECOVERY_ATTRIBUTE);
    };

    // Expiry is autonomous: the watchdog only cleans, it never renews the hold or corrects.
    const finishRecovery = () => {
        cancelRecovery();
        schedule();
        window.dispatchEvent(new Event(VIEWPORT_CHANGE_EVENT));
    };

    const startRecovery = (fresh = false) => {
        if (fresh || !document.documentElement.hasAttribute(RECOVERY_ATTRIBUTE)) {
            if (fresh) {
                cancelRecovery();
            }

            recoveryHoldUntil = window.performance.now() + (2 * SCROLL_SETTLE_MS);
            document.documentElement.setAttribute(RECOVERY_ATTRIBUTE, 'settling');
        }

        if (recoveryWatchdog) {
            return;
        }

        const generation = recoveryGeneration;
        const watch = () => {
            recoveryWatchdog = 0;
            if (generation !== recoveryGeneration
                || !document.documentElement.hasAttribute(RECOVERY_ATTRIBUTE)) {
                return;
            }

            if (window.performance.now() >= recoveryHoldUntil && !strictPanContext()) {
                finishRecovery();
                return;
            }

            recoveryWatchdog = window.setTimeout(watch, SCROLL_SETTLE_MS);
        };
        recoveryWatchdog = window.setTimeout(watch, SCROLL_SETTLE_MS);
    };

    // Outer scroll only: keep X and nested scrollers, once per settled posture.
    const resetRootPanOnce = () => {
        if (scrollCorrectionUsed
            || !canCorrectPannedRoot()
            || !isRootPanned()) {
            return;
        }

        scrollCorrectionUsed = true;
        correctedWidth = window.innerWidth;
        startRecovery(true);
        recoveryHoldUntil = window.performance.now() + (2 * SCROLL_SETTLE_MS);
        scrollSettleAt = window.performance.now();
        window.scrollTo(window.scrollX, 0);
    };

    // A measurement counts only when it fits the current layout; an oversized or
    // out-of-bounds report is a broken frame, not a new resting band.
    const measureBand = (recovering) => {
        const viewport = window.visualViewport;
        const layoutHeight = window.innerHeight;
        if (!viewport || !isPositive(layoutHeight)) {
            return null;
        }

        const scale = viewport.scale > 1 ? viewport.scale : 1;
        const ceiling = layoutHeight / scale;
        const top = viewport.offsetTop;
        const height = viewport.height;
        const usable = isFinite(top) && isFinite(height)
            && top >= 0 && height > 0
            && top + height <= ceiling + BAND_TOLERANCE;
        if (!usable) {
            return null;
        }

        // A panned frame reports a height that does not belong to this layout; keep the
        // last unpanned band instead of adopting the contaminated measurement.
        const docHeight = document.documentElement.clientHeight;
        if (recovering
            && isPositive(docHeight)
            && docHeight > layoutHeight + BAND_TOLERANCE) {
            return null;
        }

        const roundedHeight = Math.round(Math.min(height, layoutHeight - top));
        return roundedHeight > 0
            ? { top: Math.round(top), height: roundedHeight }
            : null;
    };

    // A retained band is re-clamped to the current layout, never widened to it blindly.
    const clampBand = (band, layoutHeight) => {
        if (!band || !isPositive(layoutHeight)) {
            return null;
        }

        const top = Math.min(Math.max(0, band.top), Math.max(0, Math.round(layoutHeight) - 1));
        const height = Math.min(Math.max(1, band.height), Math.max(1, Math.round(layoutHeight) - top));
        return { top, height };
    };

    // A transient zero or invalid visual viewport keeps the last usable band.
    const resolveBand = (measured, layoutHeight) => {
        if (measured) {
            lastBand = measured;
            return measured;
        }

        const retained = clampBand(lastBand, layoutHeight);
        if (retained) {
            return retained;
        }

        // Nothing measured yet: the current view height is the only trustworthy band.
        return isPositive(layoutHeight) ? { top: 0, height: Math.round(layoutHeight) } : null;
    };

    const publish = (keyboard, band) => {
        const signature = keyboard && band ? `${band.top}:${band.height}` : 'closed';
        if (signature === publishedSignature) {
            return;
        }

        publishedSignature = signature;
        window.dispatchEvent(new Event(VIEWPORT_CHANGE_EVENT));
    };

    const apply = () => {
        frameHandle = 0;
        const native = nativeState();
        const editing = isEditing();
        const layoutHeight = window.innerHeight;
        const validLayout = isPositive(layoutHeight);
        const viewport = window.visualViewport;
        const vvValid = validLayout && !!viewport && isPositive(viewport.height);

        // Recovery is a narrow, explicit state: it may hold through the settle window so a
        // corrected pan is not re-read as a real short layout, but it never activates on a
        // client/inner mismatch alone (overflow, zoom, non-dialog and idle contexts opt out).
        const panningNow = strictPanContext();
        if (panningNow) {
            startRecovery();
        } else if (document.documentElement.hasAttribute(RECOVERY_ATTRIBUTE)
            && window.performance.now() >= recoveryHoldUntil) {
            finishRecovery();
        }

        const recoveryScope = rawScaleIsOne()
            && (keyboardEpisode || recoveryBand !== null || lastBand !== null)
            && (panningNow || window.performance.now() < recoveryHoldUntil);

        // A fold or rotation invalidates the width-specific resting root only. The
        // keyboard evidence and the idle baseline belong to the episode, not the width.
        if (Math.abs(window.innerWidth - lastWidth) > 1) {
            lastWidth = window.innerWidth;
            restRoot = null;
            restorationWitness = null;
            if (!keyboardEpisode && !recoveryBand && validLayout) {
                idleHeight = layoutHeight;
            }

            // Width callbacks arrive in bursts. Only a settled posture earns a fresh
            // correction; the counter is not replenished by resize, scroll or hints.
            posturePending = true;
            postureGeneration += 1;
            const generation = postureGeneration;
            window.clearTimeout(postureTimer);
            postureTimer = window.setTimeout(() => {
                postureTimer = 0;
                if (generation !== postureGeneration) {
                    return;
                }

                posturePending = false;
                postureSettledWidth = window.innerWidth;
                if (postureSettledWidth !== correctedWidth) {
                    scrollCorrectionUsed = false;
                }

                schedule();
            }, SCROLL_SETTLE_MS);
        }

        const scale = viewport && viewport.scale > 1 ? viewport.scale : 1;
        const unobstructedHeight = validLayout ? window.innerHeight / scale : 0;
        // A zoomed viewport is shorter by the scale alone, so count only the rest.
        const occluded = vvValid ? Math.max(0, unobstructedHeight - viewport.height) : 0;
        // Some webviews shrink the view instead of occluding it; count that height too.
        const shrunk = coarsePointer.matches && validLayout
            ? Math.max(0, idleHeight - window.innerHeight)
            : 0;
        const hiddenBottom = Math.max(occluded, shrunk);
        const geometric = editing && hiddenBottom > MIN_KEYBOARD_OCCLUSION;

        let keyboard;
        if (native === 'open') {
            dismissedByNative = false;

            if (recoveryBand) {
                // A real open resumes from the band the checkpoint kept.
                lastBand = recoveryBand;
                recoveryBand = null;
                recoveryRestHeight = 0;
            }

            // Presence, not a height: a floating keyboard leaves the view at full height.
            keyboard = true;
        } else if (native === 'closed') {
            keyboard = false;
        } else if (native === 'unknown') {
            // An ambiguous native frame is not a dismissal while the episode stands,
            // but it cannot revive a keyboard the native view already reported gone.
            keyboard = keyboardEpisode || (geometric && !dismissedByNative);
        } else {
            keyboard = geometric;
        }

        // Settlement needs a measurement that passes the same bounds check as publishing;
        // a broken report must not be accepted as proof that the keyboard went away.
        const measured = measureBand();
        const layoutFull = !!measured
            && measured.top <= BAND_TOLERANCE
            && measured.height >= Math.round(layoutHeight / scale) - BAND_TOLERANCE;
        // The iOS container closes the keyboard itself, so a closed hint with a valid full
        // CURRENT viewport is a real dismissal even when the old idle height was larger.
        // Browsers without the native hint keep the legacy coverage heuristic below.
        let settledFull = validLayout && !!measured
            && (native === 'closed' && layoutFull
                || hiddenBottom <= MIN_KEYBOARD_OCCLUSION);

        // A native hint is authoritative; the plain-browser witness belongs to absent only.
        if (native !== 'absent') {
            restorationWitness = null;
        }

        const fullCurrent = validLayout && !!measured && layoutFull && rawScaleIsOne();
        const noRecovery = !panningNow && window.performance.now() >= recoveryHoldUntil;
        const settledPosture = !posturePending
            && Math.abs(window.innerWidth - postureSettledWidth) <= 1;
        const postRotation = native === 'absent'
            && keyboardEpisode
            && noRecovery
            && settledPosture
            && Math.abs(window.innerWidth - episodeWidth) > 1;

        if (restorationWitness
            && Math.abs(window.innerWidth - restorationWitness.width) > 1) {
            restorationWitness = null;
        }

        // A shorter layout alone is ambiguous; only growth after a settled rotation restores.
        if (postRotation && fullCurrent && restorationWitness
            && measured.height >= restorationWitness.height + MIN_KEYBOARD_OCCLUSION) {
            keyboard = false;
            settledFull = true;
        } else if (postRotation && fullCurrent && !restorationWitness) {
            restorationWitness = { width: window.innerWidth, height: measured.height };
        }

        // WillHide can land before the geometry settles. That close is a checkpoint:
        // the visible keyboard is gone, but the band the reopen needs is not.
        if (native === 'closed') {
            dismissedByNative = true;

            if (!settledFull && (keyboardEpisode || lastBand)) {
                recoveryBand = clampBand(lastBand, layoutHeight) ?? recoveryBand;
                recoveryRestHeight = episodeRestHeight || recoveryRestHeight || idleHeight;
                recoveryWidth = window.innerWidth;
                recoveryViewportHeight = vvValid ? viewport.height : 0;
            }

            if (settledFull) {
                recoveryBand = null;
                recoveryRestHeight = 0;
            }

            if (keyboardEpisode) {
                keyboardEpisode = false;
                episodeRestHeight = 0;
                restorationWitness = null;
                scrollCorrectionUsed = false;
                lastBand = null;
            }

            if (settledFull && layoutHeight >= idleHeight - SCROLL_RESET_TOLERANCE) {
                idleHeight = layoutHeight;
            }
        } else if (native === 'unknown' && !keyboardEpisode && recoveryBand) {
            if (!isEditing()) {
                recoveryBand = null;
                recoveryRestHeight = 0;
            } else if (settledFull) {
                recoveryBand = null;
                recoveryRestHeight = 0;
                if (layoutHeight >= idleHeight - SCROLL_RESET_TOLERANCE) {
                    idleHeight = layoutHeight;
                }
            } else if (!vvValid
                ? window.innerWidth !== recoveryWidth
                : hiddenBottom > MIN_KEYBOARD_OCCLUSION
                    && (window.innerWidth !== recoveryWidth || viewport.height !== recoveryViewportHeight)) {
                // Only a new posture or new coverage resumes the interrupted episode.
                keyboardEpisode = true;
                episodeWidth = window.innerWidth;
                restorationWitness = null;
                episodeRestHeight = recoveryRestHeight;
                lastBand = recoveryBand;
                recoveryBand = null;
                recoveryRestHeight = 0;
                dismissedByNative = false;
                keyboard = true;
            }
        }

        if (keyboard) {
            if (!keyboardEpisode) {
                keyboardEpisode = true;
                episodeWidth = window.innerWidth;
                restorationWitness = null;
                // No restRoot on a real overflow; use the preserved idle height.
                episodeRestHeight = restRoot ? restRoot.clientHeight : idleHeight;
            }

            resetRootPanOnce();
            // Wait for the native inset to drop before publishing or revealing.
            if (scrollSettleAt) {
                if (window.performance.now() - scrollSettleAt < SCROLL_SETTLE_MS) {
                    schedule();
                    return;
                }

                scrollSettleAt = 0;
            }
        } else {
            scrollSettleAt = 0;
            if (keyboardEpisode && native !== 'open' && settledFull) {
                keyboardEpisode = false;
                episodeRestHeight = 0;
                restorationWitness = null;
                scrollCorrectionUsed = false;
                lastBand = null;
                // Never promote a keyboard-shrunken height to the resting baseline.
                if (layoutHeight >= idleHeight - SCROLL_RESET_TOLERANCE) {
                    idleHeight = layoutHeight;
                }
            }

            if (!keyboardEpisode) {
                if (validLayout
                    && (native === 'absent' || layoutHeight >= idleHeight - SCROLL_RESET_TOLERANCE)) {
                    idleHeight = layoutHeight;
                }

                rememberRestRoot();
            }
        }

        const active = document.activeElement;
        // The title row and selection toolbar leave no room in a landscape band.
        const editorInHeader = keyboard
            && active instanceof HTMLElement
            && active.closest('.app-tabpanel-header') !== null;
        document.documentElement.classList.toggle(KEYBOARD_CLASS, keyboard);
        document.documentElement.classList.toggle(EDITOR_IN_HEADER_CLASS, editorInHeader);

        if (keyboard) {
            const bandBound = layoutReference(validLayout ? layoutHeight : 0, recoveryScope);
            const band = resolveBand(measureBand(recoveryScope), validLayout ? bandBound : 0);
            const panPending = recoveryScope;
            if (band) {
                document.documentElement.style.setProperty(HEIGHT_VARIABLE, `${band.height}px`);
                document.documentElement.style.setProperty(TOP_INSET_VARIABLE, `${band.top}px`);
                if (!panPending) {
                    window.requestAnimationFrame(() => revealFocusedEditor(band));
                    window.setTimeout(() => revealFocusedEditor(band), 150);
                }

                publish(true, band);
            }

            return;
        }

        removeGeometry();
        cancelRecovery();
        publish(false, null);
    };

    const schedule = () => {
        if (frameHandle) {
            return;
        }

        frameHandle = window.requestAnimationFrame(apply);
    };

    const scheduleAfterFocusChange = () => window.setTimeout(schedule, 120);

    window.visualViewport?.addEventListener('resize', schedule);
    window.visualViewport?.addEventListener('scroll', schedule);
    window.addEventListener('resize', schedule);
    window.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('orientationchange', () => {
        // The width baseline belongs to apply(); the episode outlives the rotation.
        restRoot = null;
        schedule();
    });
    const scheduleFromNative = () => schedule();
    window.addEventListener(NATIVE_CHANGE_EVENT, scheduleFromNative);
    // A native host may dispatch on the document instead; custom events do not bubble to window.
    document.addEventListener(NATIVE_CHANGE_EVENT, scheduleFromNative);
    // A native view can shrink after focus with no visual viewport event.
    document.addEventListener('focusin', () => {
        schedule();
        window.setTimeout(schedule, 300);
    });
    document.addEventListener('focusout', scheduleAfterFocusChange);
    schedule();
})();
