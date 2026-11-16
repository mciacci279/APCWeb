// APC Web — atajos de teclado y utilidades mínimas
window.apc = (function () {
  let ref = null;

  function onKey(e) {
    if (!ref) return;
    if (e.key === 'F2') { e.preventDefault(); enfocar('buscar'); }
    else if (e.key === 'F3') { e.preventDefault(); ref.invokeMethodAsync('AtajoCliente'); }
    // Teclas de emisión, iguales a APC.WinForms
    else if (e.key === 'F8' && !e.ctrlKey) { e.preventDefault(); ref.invokeMethodAsync('AtajoFacturaTicket'); }
    else if (e.key === 'F9' && !e.ctrlKey) { e.preventDefault(); ref.invokeMethodAsync('AtajoFacturaA4'); }
    else if (e.key === 'F9' && e.ctrlKey) { e.preventDefault(); ref.invokeMethodAsync('AtajoPedidoA4'); }
    else if (e.key === 'F10' && e.ctrlKey) { e.preventDefault(); ref.invokeMethodAsync('AtajoPedidoTicket'); }
    else if (e.key === 'F11' && e.ctrlKey) { e.preventDefault(); ref.invokeMethodAsync('AtajoPedidoSinImprimir'); }
    else if (e.key === 'F12' && e.ctrlKey) { e.preventDefault(); ref.invokeMethodAsync('AtajoPresupuesto'); }
    else if (e.key === 'Enter') {
      const t = e.target;
      const enCampo = t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA' || t.tagName === 'BUTTON');
      if (!enCampo) ref.invokeMethodAsync('AtajoNueva');
    }
  }

  function enfocar(id) {
    const el = document.getElementById(id);
    if (el && !el.disabled) { el.focus(); if (el.select) el.select(); }
  }

  return {
    registrarAtajos: function (dotnetRef) {
      ref = dotnetRef;
      document.removeEventListener('keydown', onKey);
      document.addEventListener('keydown', onKey);
    },
    quitarAtajos: function () {
      document.removeEventListener('keydown', onKey);
      ref = null;
    },
    enfocar: enfocar,
    // Copia un texto al portapapeles (links para mandarle al cliente).
    copiar: function (texto) {
      if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(texto); return; }
      const a = document.createElement('textarea');
      a.value = texto;
      a.style.cssText = 'position:fixed;left:-10000px;top:0';
      document.body.appendChild(a);
      a.select();
      try { document.execCommand('copy'); } catch (e) { }
      document.body.removeChild(a);
    },
    // Descarga un archivo generado por el servidor (reportes en Excel/CSV).
    descargar: function (nombre, base64, tipo) {
      const binario = atob(base64);
      const bytes = new Uint8Array(binario.length);
      for (let i = 0; i < binario.length; i++) bytes[i] = binario.charCodeAt(i);
      const url = URL.createObjectURL(new Blob([bytes], { type: tipo || 'text/csv' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = nombre;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    },
    // Carga el comprobante en un marco oculto: la página se imprime sola al cargar,
    // sin abrir pestañas (los bloqueadores de ventanas emergentes no intervienen).
    imprimir: function (url) {
      let f = document.getElementById('apc-impresion');
      if (!f) {
        f = document.createElement('iframe');
        f.id = 'apc-impresion';
        f.title = 'Impresión';
        f.setAttribute('aria-hidden', 'true');
        f.tabIndex = -1;
        f.style.cssText = 'position:fixed;left:-10000px;top:0;width:210mm;height:297mm;border:0;';
        document.body.appendChild(f);
      }
      f.src = url + (url.indexOf('?') >= 0 ? '&' : '?') + 't=' + Date.now();
    }
  };
})();

// Páginas de impresión: abrir el diálogo de impresión al cargar
window.addEventListener('load', function () {
  if (document.querySelector('[data-autoprint="true"]')) {
    // Algunos navegadores usan el título de la ventana principal como nombre del PDF:
    // mientras dura la impresión le ponemos el del comprobante (ej. "Factura B 00012-00000123 - CUIT ...").
    let padre = null, tituloPadre = null;
    try { if (window.parent && window.parent !== window) { padre = window.parent.document; tituloPadre = padre.title; } } catch (e) { padre = null; }
    if (padre) {
      padre.title = document.title;
      window.addEventListener('afterprint', function () { padre.title = tituloPadre; }, { once: true });
    }
    setTimeout(function () { window.print(); }, 300);
  }
});
