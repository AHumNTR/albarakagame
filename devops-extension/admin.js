const API_BASE = "https://test.humn.tr";

async function getAuthToken() {
	if (window.SDK && typeof SDK.getAccessToken === 'function') {
		try {
			return await Promise.race([
				SDK.getAccessToken(),
				new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 1000))
			]);
		} catch (e) {
			return '';
		}
	}
	return '';
}

async function trainModel() {
	const btn = document.getElementById('btnTrain');
	const label = document.getElementById('trainResult');
	if (btn) btn.disabled = true;
	if (label) {
		label.style.color = 'var(--muted)';
		label.innerText = 'Eğitim başlatıldı, bu işlem birkaç dakika sürebilir...';
	}

	const token = await getAuthToken();
	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	try {
		const res = await fetch(`${API_BASE}/api/admin/train-model`, {
			method: 'POST',
			headers
		});
		const data = await res.json();

		if (res.ok) {
			if (label) {
				label.style.color = '#2dd4bf';
				label.innerText = data.message || 'Model başarıyla eğitildi ve yüklendi!';
			}
		} else {
			if (label) {
				label.style.color = '#f87171';
				label.innerText = data.message || data.detail || 'Eğitim başarısız oldu.';
			}
		}
	} catch (err) {
		if (label) {
			label.style.color = '#f87171';
			label.innerText = 'Sunucuya bağlanılamadı veya işlem zaman aşımına uğradı.';
		}
	} finally {
		if (btn) btn.disabled = false;
	}
}
async function syncTeam() {
	const btn = document.getElementById('btnSync');
	const label = document.getElementById('syncResult');
	if (btn) btn.disabled = true;
	if (label) {
		label.style.color = 'var(--muted)';
		label.innerText = 'Senkronize ediliyor...';
	}

	const token = await getAuthToken();
	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	try {
		const res = await fetch(`${API_BASE}/api/admin/sync-team`, {
			method: 'POST',
			headers
		});
		const data = await res.json();

		if (res.ok) {
			if (label) {
				label.style.color = '#2dd4bf';
				label.innerText = `Tamamlandı: ${data.added} eklendi, ${data.removed} silindi. Toplam: ${data.activeUsers}`;
			}
		} else {
			if (label) {
				label.style.color = '#f87171';
				label.innerText = data.detail || 'İşlem başarısız oldu.';
			}
		}
	} catch (err) {
		if (label) {
			label.style.color = '#f87171';
			label.innerText = 'Sunucuya bağlanılamadı.';
		}
	} finally {
		if (btn) btn.disabled = false;
	}
}
async function loadAdminComments() {
	const tbody = document.getElementById('commentsTableBody');
	if (!tbody) return;

	const token = await getAuthToken();
	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	try {
		const res = await fetch(`${API_BASE}/api/admin/comments?page=1&pageSize=30`, { headers });
		if (!res.ok) {
			tbody.innerHTML = `<tr><td colspan="5" style="text-align:center; padding:20px; color:#f87171;">Yorumlar yüklenemedi (${res.status}).</td></tr>`;
			return;
		}

		const data = await res.json();
		if (!data.items || !data.items.length) {
			tbody.innerHTML = '<tr><td colspan="5" style="text-align:center; padding:20px; color:var(--muted);">Henüz değerlendirilen yorum yok.</td></tr>';
			return;
		}

		tbody.innerHTML = data.items.map(c => {
			const activeScore = c.correctedScore != null ? c.correctedScore : c.predictedScore;
			const addedBadge = c.isAddedToDataset
				? '<span style="background:#065f46; color:#34d399; padding:2px 6px; border-radius:4px; font-size:0.75rem;">Eklendi</span>'
				: `<button class="btn-refresh" style="font-size:0.75rem; padding:4px 8px;" onclick="addToDataset(${c.id})">Veri Setine Ekle</button>`;

			return `
				<tr style="border-bottom: 1px solid var(--border);">
					<td style="padding: 10px; vertical-align: top;">
						<div style="font-weight:700; color:#fff;">#${c.workItemId || '---'}</div>
						<div style="color:var(--muted); font-size:0.75rem;">${escapeHtml(c.title || '')}</div>
					</td>
					<td style="padding: 10px; vertical-align: top; max-width: 350px; word-break: break-word;">
						${escapeHtml(c.detail)}
					</td>
					<td style="padding: 10px; vertical-align: top;">
						<span style="color:#2dd4bf; font-weight:700;">${Math.round(c.modelScore)}p</span>
						<span style="color:var(--muted); font-size:0.75rem;">(${c.predictedScore}/5)</span>
					</td>
					<td style="padding: 10px; vertical-align: top;">
						<select id="scoreSelect_${c.id}" onchange="saveCorrection(${c.id})" style="background:var(--card-sub); color:#fff; border:1px solid var(--border); border-radius:4px; padding:4px 6px;">
							${[1, 2, 3, 4, 5].map(v => `<option value="${v}" ${v === activeScore ? 'selected' : ''}>${v}</option>`).join('')}
						</select>
					</td>
					<td style="padding: 10px; vertical-align: top; text-align: right;" id="actionCell_${c.id}">
						${addedBadge}
					</td>
				</tr>
			`;
		}).join('');
	} catch (err) {
		tbody.innerHTML = '<tr><td colspan="5" style="text-align:center; padding:20px; color:#f87171;">Bağlantı hatası oluştu.</td></tr>';
	}
}

async function saveCorrection(id) {
	const sel = document.getElementById(`scoreSelect_${id}`);
	if (!sel) return;

	const token = await getAuthToken();
	const headers = {
		'Content-Type': 'application/json',
		...(token ? { 'Authorization': `Bearer ${token}` } : {})
	};

	try {
		await fetch(`${API_BASE}/api/admin/comments/${id}/correct`, {
			method: 'POST',
			headers,
			body: JSON.stringify({ score: parseInt(sel.value, 10) })
		});
	} catch (e) {
		console.error("Düzeltme kaydedilemedi:", e);
	}
}

async function addToDataset(id) {
	const cell = document.getElementById(`actionCell_${id}`);
	if (cell) cell.innerHTML = '<span style="color:var(--muted); font-size:0.75rem;">Ekleniyor...</span>';

	const token = await getAuthToken();
	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	try {
		const res = await fetch(`${API_BASE}/api/admin/comments/${id}/add-to-dataset`, {
			method: 'POST',
			headers
		});

		if (res.ok) {
			if (cell) cell.innerHTML = '<span style="background:#065f46; color:#34d399; padding:2px 6px; border-radius:4px; font-size:0.75rem;">Eklendi</span>';
		} else {
			if (cell) cell.innerHTML = '<span style="color:#f87171; font-size:0.75rem;">Hata</span>';
		}
	} catch (e) {
		if (cell) cell.innerHTML = '<span style="color:#f87171; font-size:0.75rem;">Hata</span>';
	}
}
// Load scoring settings from GET /api/settings
async function loadSettings() {
	const token = await getAuthToken();
	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	try {
		const res = await fetch(`${API_BASE}/api/settings`, { headers });
		if (!res.ok) return;
		const data = await res.json();

		document.getElementById('settingComments').checked = !!data.commentsEnabled;
		document.getElementById('settingCompleted').checked = !!data.completedWorkEnabled;
		document.getElementById('settingRemaining').checked = !!data.remainingWorkEnabled;
		document.getElementById('settingState').checked = !!data.stateChangedEnabled;
		document.getElementById('settingIteration').checked = !!data.iterationChangedEnabled;
	} catch (e) {
		console.error("Ayarlar yüklenemedi:", e);
	}
}

// Save scoring settings to POST /api/settings
async function saveSettings() {
	const btn = document.getElementById('btnSaveSettings');
	const label = document.getElementById('settingsResult');
	if (btn) btn.disabled = true;
	if (label) {
		label.style.color = 'var(--muted)';
		label.innerText = 'Kaydediliyor...';
	}

	const token = await getAuthToken();
	const headers = {
		'Content-Type': 'application/json',
		...(token ? { 'Authorization': `Bearer ${token}` } : {})
	};

	const payload = {
		commentsEnabled: document.getElementById('settingComments').checked,
		completedWorkEnabled: document.getElementById('settingCompleted').checked,
		remainingWorkEnabled: document.getElementById('settingRemaining').checked,
		stateChangedEnabled: document.getElementById('settingState').checked,
		iterationChangedEnabled: document.getElementById('settingIteration').checked
	};

	try {
		const res = await fetch(`${API_BASE}/api/settings`, {
			method: 'POST',
			headers,
			body: JSON.stringify(payload)
		});

		if (res.ok) {
			if (label) {
				label.style.color = '#2dd4bf';
				label.innerText = 'Ayarlar başarıyla kaydedildi.';
			}
		} else {
			if (label) {
				label.style.color = '#f87171';
				label.innerText = `Kaydedilemedi (${res.status}). Yetkiniz olmayabilir.`;
			}
		}
	} catch (e) {
		if (label) {
			label.style.color = '#f87171';
			label.innerText = 'Sunucuya bağlanılamadı.';
		}
	} finally {
		if (btn) btn.disabled = false;
	}
}

// Test comment score against POST /api/test-score
async function testCommentScore() {
	const title = document.getElementById('testCommentTitle').value.trim();
	const comment = document.getElementById('testCommentBody').value.trim();
	const label = document.getElementById('testCommentResult');
	const btn = document.getElementById('btnTestComment');

	if (!comment) {
		if (label) {
			label.style.color = '#f87171';
			label.innerText = 'Lütfen bir yorum metni girin.';
		}
		return;
	}

	if (btn) btn.disabled = true;
	if (label) {
		label.style.color = 'var(--muted)';
		label.innerText = 'Model değerlendiriyor...';
	}

	const token = await getAuthToken();
	const headers = {
		'Content-Type': 'application/json',
		...(token ? { 'Authorization': `Bearer ${token}` } : {})
	};

	try {
		const res = await fetch(`${API_BASE}/api/test-score`, {
			method: 'POST',
			headers,
			body: JSON.stringify({ title, comment })
		});

		if (!res.ok) {
			if (label) {
				label.style.color = '#f87171';
				label.innerText = `Hata: ${res.status}`;
			}
			return;
		}

		const data = await res.json();
		const rawScore = Math.round(data.score ?? 0);
		const passed = data.passed;
		const points = data.pointsAwarded ?? (passed ? Math.round((rawScore / 100) * 50) : 0);

		if (label) {
			label.style.color = passed ? '#2dd4bf' : '#f87171';
			label.innerHTML = `<strong>${rawScore}/100</strong> &bull; ${passed ? 'GEÇTİ' : 'KALDI'} &bull; Tahmini Puan: <strong>+${points}p</strong>`;
		}
	} catch (e) {
		if (label) {
			label.style.color = '#f87171';
			label.innerText = 'Sunucuya bağlanılamadı.';
		}
	} finally {
		if (btn) btn.disabled = false;
	}
}
function escapeHtml(s) {
	return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Call during initial load
if (window.SDK && typeof SDK.init === 'function') {
	SDK.init({ applyTheme: true });
	const readyTimeout = new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 1000));

	Promise.race([SDK.ready(), readyTimeout])
		.then(() => {
			SDK.notifyLoadSucceeded();
			loadAdminComments();
			loadSettings();
		})
		.catch(() => {
			loadAdminComments();
			loadSettings();
		});
} else {
	loadAdminComments();
}
