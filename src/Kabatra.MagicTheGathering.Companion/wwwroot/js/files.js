// Saving files from the app, e.g. a deck backup. Loaded as a module when first needed.

/** Offers the browser's "save file" for some text. */
export function saveTextFile(fileName, text, mimeType) {
    const blob = new Blob([text], { type: mimeType || "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
