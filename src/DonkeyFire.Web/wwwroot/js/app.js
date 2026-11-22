// Browser helpers called from Blazor through IJSRuntime.
window.bt = (function () {
    return {
        download: function (filename, text, type) {
            // BOM so Excel opens UTF-8 CSV correctly.
            const blob = new Blob(["﻿", text], { type: type || "text/csv;charset=utf-8" });
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url; a.download = filename;
            document.body.appendChild(a); a.click(); a.remove();
            setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
        },

        print: function () { window.print(); },

        copy: async function (text) {
            try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
        },

        toggleNav: function (open) {
            document.querySelector(".app")?.classList.toggle("nav-open", open);
        },
    };
})();
