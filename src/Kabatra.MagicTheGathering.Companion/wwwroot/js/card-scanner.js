// Camera + OCR for scanning physical cards. Text recognition runs in the browser with Tesseract.js,
// so photos never leave the device; only the text that was read is sent to Scryfall.
//
// The user lines the card up with a card-shaped guide over the video, so we know roughly where
// the title line and the bottom-left set/number text are, and only read those two strips.

const TESSERACT_URL = "https://cdn.jsdelivr.net/npm/tesseract.js@5.1.1/dist/tesseract.min.js";

// Where text sits on a modern card, as fractions of the card's width and height (with some slack
// for a card that isn't lined up perfectly).
const TITLE = { x: 0.065, y: 0.045, w: 0.68, h: 0.08 };
const BOTTOM = { x: 0.025, y: 0.885, w: 0.46, h: 0.085 };
const CARD_ASPECT = 63 / 88;

let tesseractLoad = null;
let workerLoad = null;

function loadTesseract() {
    tesseractLoad ??= new Promise((resolve, reject) => {
        if (window.Tesseract) return resolve(window.Tesseract);
        const script = document.createElement("script");
        script.src = TESSERACT_URL;
        script.crossOrigin = "anonymous";
        script.onload = () => resolve(window.Tesseract);
        script.onerror = () => { tesseractLoad = null; reject(new Error("Couldn't load the text reader. Check your connection.")); };
        document.head.appendChild(script);
    });
    return tesseractLoad;
}

function getWorker() {
    workerLoad ??= loadTesseract()
        .then(T => T.createWorker("eng"))
        .catch(e => { workerLoad = null; throw e; });
    return workerLoad;
}

/** Starts downloading the OCR engine and language data in the background. */
export function warmUp() {
    getWorker().catch(() => { /* reported when a scan is tried */ });
}

/** Shows the rear camera in the video element. Returns null on success, or a message to show. */
export async function startCamera(video) {
    if (!navigator.mediaDevices?.getUserMedia) {
        return window.isSecureContext
            ? "This browser can't use the camera. Try “Use a photo” instead."
            : "The camera only works over https. Try “Use a photo” instead.";
    }
    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: false,
            video: { facingMode: { ideal: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } },
        });
        video.srcObject = stream;
        video.setAttribute("playsinline", "");
        video.muted = true;
        await video.play();
        return null;
    } catch (e) {
        if (e?.name === "NotAllowedError") return "Camera permission was denied. Allow it in the browser settings, or use a photo.";
        if (e?.name === "NotFoundError" || e?.name === "OverconstrainedError") return "No camera was found. Try “Use a photo” instead.";
        return `Couldn't start the camera: ${e?.message ?? e}`;
    }
}

export function stopCamera(video) {
    const stream = video?.srcObject;
    if (stream) stream.getTracks().forEach(t => t.stop());
    if (video) video.srcObject = null;
}

/** Reads the card inside the guide from the live video. */
export async function scanVideo(video, guide) {
    if (!video.videoWidth) throw new Error("The camera isn't ready yet.");
    const card = guideRectInVideo(video, guide);
    return readCard(video, card);
}

/** Reads a card from a photo picked with a file input. The photo should be cropped to the card. */
export async function scanFile(input) {
    const file = input.files?.[0];
    if (!file) return null;
    const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
    try {
        return await readCard(bitmap, centeredCardRect(bitmap.width, bitmap.height, 1));
    } finally {
        bitmap.close?.();
        input.value = ""; // so picking the same photo again still fires a change
    }
}

async function readCard(source, card) {
    const worker = await getWorker();

    await worker.setParameters({ tessedit_pageseg_mode: "7" }); // a single line of text
    const title = (await worker.recognize(crop(source, card, TITLE, 90, "auto"))).data.text;

    // The strip may also catch the last line of rules text; the parser picks out the set/number pattern.
    await worker.setParameters({ tessedit_pageseg_mode: "6" }); // a small block of text
    const bottom = (await worker.recognize(crop(source, card, BOTTOM, 160, "auto"))).data.text;

    return { title: title.trim(), bottom: bottom.trim() };
}

/** The guide's rectangle in the video's own pixels, allowing for object-fit: cover. */
function guideRectInVideo(video, guide) {
    const v = video.getBoundingClientRect();
    const g = guide.getBoundingClientRect();
    const scale = Math.max(v.width / video.videoWidth, v.height / video.videoHeight);
    const offsetX = (v.width - video.videoWidth * scale) / 2;
    const offsetY = (v.height - video.videoHeight * scale) / 2;
    return {
        x: (g.left - v.left - offsetX) / scale,
        y: (g.top - v.top - offsetY) / scale,
        w: g.width / scale,
        h: g.height / scale,
    };
}

function centeredCardRect(width, height, fill) {
    let h = height * fill;
    let w = h * CARD_ASPECT;
    if (w > width * fill) { w = width * fill; h = w / CARD_ASPECT; }
    return { x: (width - w) / 2, y: (height - h) / 2, w, h };
}

/**
 * Cuts one strip out of the card, scales it up for OCR, and stretches the contrast.
 * invert: true/false, or "auto" to invert dark strips so the text comes out dark on light.
 */
function crop(source, card, part, targetHeight, invert) {
    const sx = card.x + part.x * card.w, sy = card.y + part.y * card.h;
    const sw = part.w * card.w, sh = part.h * card.h;
    const scale = Math.max(1, targetHeight / sh);
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(sw * scale);
    canvas.height = Math.round(sh * scale);
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(source, sx, sy, sw, sh, 0, 0, canvas.width, canvas.height);

    const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
    const px = image.data;
    let min = 255, max = 0, sum = 0;
    const gray = new Uint8ClampedArray(px.length / 4);
    for (let i = 0, j = 0; i < px.length; i += 4, j++) {
        const g = 0.299 * px[i] + 0.587 * px[i + 1] + 0.114 * px[i + 2];
        gray[j] = g;
        sum += g;
        if (g < min) min = g;
        if (g > max) max = g;
    }
    // Tesseract reads dark text on a light background best.
    if (invert === "auto") invert = sum / gray.length < 110;
    const range = Math.max(1, max - min);
    for (let i = 0, j = 0; i < px.length; i += 4, j++) {
        let g = ((gray[j] - min) / range) * 255;
        if (invert) g = 255 - g;
        px[i] = px[i + 1] = px[i + 2] = g;
    }
    ctx.putImageData(image, 0, 0);
    return canvas;
}
