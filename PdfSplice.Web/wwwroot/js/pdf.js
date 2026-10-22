/*
 * PdfSplice 前端脚本（从财务工具箱 toolbox.js 的 PDF 段整段搬迁）
 * ------------------------------------------------------------------
 *  - 文件通过普通 HTTP 端点 /api/pdf/* 上传下载，不经 SignalR（避开 32KB 单消息上限）。
 *  - 文件列表用 DataTransfer 维护：支持多次选择追加 / 拖拽追加 / 单个移除 / 清空。
 *  - init() 幂等，重复调用不会重复绑定。
 */
(function () {
    'use strict';

    function $(id) { return document.getElementById(id); }

    function postAndDownload(url, formData, statusEl, busyLabel) {
        if (statusEl) { statusEl.textContent = busyLabel || '处理中…'; }
        return fetch(url, { method: 'POST', body: formData })
            .then(function (resp) {
                if (!resp.ok) return resp.text().then(function (t) { throw new Error(t || ('HTTP ' + resp.status)); });
                // 从 Content-Disposition 取文件名，兜底用默认名
                var cd = resp.headers.get('Content-Disposition') || '';
                var m = /filename\*?=(?:UTF-8'')?["']?([^;"']+)/i.exec(cd);
                var fileName = m ? decodeURIComponent(m[1]) : 'download.bin';
                return resp.blob().then(function (blob) {
                    var a = document.createElement('a');
                    a.href = URL.createObjectURL(blob);
                    a.download = fileName;
                    document.body.appendChild(a);
                    a.click();
                    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
                    if (statusEl) statusEl.textContent = '完成 ✓';
                });
            })
            .catch(function (err) {
                if (statusEl) statusEl.textContent = '失败：' + err.message;
            });
    }

    function buildPdfForm() {
        var files = $('toolbox-pdf-files').files;
        var fd = new FormData();
        for (var i = 0; i < files.length; i++) fd.append('files', files[i]);
        return { fd: fd, count: files.length };
    }

    // ---------- PDF ----------
    // 文件列表用 DataTransfer 维护：input.files 是只读 FileList，无法直接增删，
    // 只能通过 DataTransfer 攒好文件后整体赋回 input.files。支持多次选择追加 / 拖拽追加 / 单个移除 / 清空。
    var _pdfDt = null;
    function pdfEnsureDt() {
        if (!_pdfDt) {
            try { _pdfDt = new DataTransfer(); }
            catch (e) { _pdfDt = null; }
        }
        return _pdfDt;
    }
    // 把若干 File 追加进 DataTransfer 并同步到原生 input，随后刷新列表 UI。
    function pdfAddFiles(fileList) {
        var dt = pdfEnsureDt();
        var added = 0;
        if (fileList) {
            for (var i = 0; i < fileList.length; i++) {
                var f = fileList[i];
                // 只接受 PDF（accept 已限定，拖拽时兜底过滤）
                if (f.type === 'application/pdf' || /\.pdf$/i.test(f.name)) {
                    if (dt) dt.items.add(f);
                    added++;
                }
            }
        }
        var filesEl = $('toolbox-pdf-files');
        // 不支持 DataTransfer 的老浏览器：直接用原生 input 的 FileList，无法追加/删除，但基本按钮仍可用
        if (dt && filesEl) filesEl.files = dt.files;
        pdfRenderList();
        if (added > 0) pdfSetStatus('已添加 ' + added + ' 个文件');
    }
    function pdfRemoveAt(idx) {
        var dt = pdfEnsureDt();
        if (!dt) return;
        dt.items.remove(idx);
        var filesEl = $('toolbox-pdf-files');
        if (filesEl) filesEl.files = dt.files;
        pdfRenderList();
    }
    function pdfClear() {
        var dt = pdfEnsureDt();
        var filesEl = $('toolbox-pdf-files');
        if (dt) dt.items.clear();
        if (filesEl) filesEl.value = '';   // 清空原生 input，确保同一文件可再次选入
        pdfRenderList();
        pdfSetStatus('已清空');
    }
    function pdfSetStatus(text) {
        var status = $('toolbox-pdf-status');
        if (status) status.textContent = text;
    }
    // 友好文件大小：B / KB / MB
    function pdfFmtSize(bytes) {
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        return (bytes / 1024 / 1024).toFixed(2) + ' MB';
    }
    // 渲染文件列表 + 计数徽标 + 清空按钮可用性。序号即合并顺序。
    function pdfRenderList() {
        var listEl = $('toolbox-pdf-list');
        var countEl = $('toolbox-pdf-count');
        var clearBtn = $('toolbox-pdf-clear');
        var filesEl = $('toolbox-pdf-files');
        if (!listEl || !filesEl) return;
        var files = filesEl.files;
        var n = files ? files.length : 0;
        if (countEl) countEl.textContent = n > 0 ? ('已选 ' + n + ' 个') : '未选择文件';
        if (clearBtn) clearBtn.disabled = (n === 0);
        listEl.innerHTML = '';
        if (n === 0) {
            var empty = document.createElement('li');
            empty.className = 'toolbox-pdf-empty';
            empty.textContent = '尚未选择 PDF 文件';
            listEl.appendChild(empty);
            return;
        }
        for (var i = 0; i < n; i++) {
            (function (idx) {
                var f = files[idx];
                var li = document.createElement('li');
                li.className = 'toolbox-pdf-item';

                var idxEl = document.createElement('span');
                idxEl.className = 'toolbox-pdf-item-idx';
                idxEl.textContent = String(idx + 1);

                var info = document.createElement('div');
                info.className = 'toolbox-pdf-item-info';
                var nameEl = document.createElement('span');
                nameEl.className = 'toolbox-pdf-item-name';
                nameEl.textContent = f.name;
                nameEl.title = f.name;
                var metaEl = document.createElement('span');
                metaEl.className = 'toolbox-pdf-item-meta';
                metaEl.textContent = pdfFmtSize(f.size);
                info.appendChild(nameEl);
                info.appendChild(metaEl);

                var rm = document.createElement('button');
                rm.type = 'button';
                rm.className = 'toolbox-pdf-item-rm';
                rm.title = '移除';
                rm.setAttribute('aria-label', '移除 ' + f.name);
                rm.innerHTML = '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><path fill="currentColor" d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>';
                rm.addEventListener('click', function () { pdfRemoveAt(idx); });

                li.appendChild(idxEl);
                li.appendChild(info);
                li.appendChild(rm);
                listEl.appendChild(li);
            })(i);
        }
    }

    function bindPdf() {
        var filesEl = $('toolbox-pdf-files');
        var status = $('toolbox-pdf-status');
        if (!filesEl || filesEl.dataset.toolboxBound === '1') return;
        filesEl.dataset.toolboxBound = '1';

        // 原生选择：把新选的一批追加进列表（不去重，用户可手动移除）
        filesEl.addEventListener('change', function () {
            if (filesEl.files && filesEl.files.length) pdfAddFiles(filesEl.files);
        });

        // 拖拽上传：拖拽到 dropzone 时高亮，放下时追加文件
        var dz = $('toolbox-pdf-dropzone');
        if (dz) {
            ['dragenter', 'dragover'].forEach(function (ev) {
                dz.addEventListener(ev, function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    dz.classList.add('is-dragover');
                });
            });
            ['dragleave', 'dragend', 'drop'].forEach(function (ev) {
                dz.addEventListener(ev, function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    dz.classList.remove('is-dragover');
                });
            });
            dz.addEventListener('drop', function (e) {
                var dt = e.dataTransfer;
                if (dt && dt.files && dt.files.length) pdfAddFiles(dt.files);
            });
        }

        var clearBtn = $('toolbox-pdf-clear');
        if (clearBtn) clearBtn.addEventListener('click', pdfClear);

        $('toolbox-pdf-combine').addEventListener('click', function () {
            var b = buildPdfForm();
            if (b.count === 0) { pdfSetStatus('请先选择 PDF 文件'); return; }
            postAndDownload('/api/pdf/combine', b.fd, status, '合并中…');
        });

        $('toolbox-pdf-split').addEventListener('click', function () {
            var b = buildPdfForm();
            if (b.count === 0) { pdfSetStatus('请先选择 PDF 文件'); return; }
            postAndDownload('/api/pdf/split', b.fd, status, '拆分中…');
        });

        $('toolbox-pdf-splice').addEventListener('click', function () {
            var b = buildPdfForm();
            if (b.count === 0) { pdfSetStatus('请先选择 PDF 文件'); return; }
            b.fd.append('top', $('toolbox-pdf-top').value);
            b.fd.append('bottom', $('toolbox-pdf-bottom').value);
            b.fd.append('left', $('toolbox-pdf-left').value);
            b.fd.append('right', $('toolbox-pdf-right').value);
            b.fd.append('maxRow', $('toolbox-pdf-maxrow').value);
            postAndDownload('/api/pdf/splice', b.fd, status, '拼版中…');
        });

        // 首屏渲染一次空态
        pdfRenderList();
    }

    // ---------- 初始化（幂等） ----------
    // Blazor Server 预渲染的静态 DOM 会在 circuit 建立后被整体替换，
    // DOMContentLoaded 时绑的事件全部丢失，所以必须由组件 OnAfterRenderAsync
    // 每次渲染后重调 init（内部有 dataset 守卫，不会重复绑定）。
    function init() { bindPdf(); }

    window.PdfSplicePdf = { init: init };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
