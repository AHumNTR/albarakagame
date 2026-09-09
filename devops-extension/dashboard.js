const API_BASE = "https://test.humn.tr";
const RANKS = {
	mil: ['Albay', 'Tuğgeneral', 'Tümgeneral', 'Orgeneral', 'Genel Kurmay Başkanı'],
	acad: ['Öğretim Görevlisi', 'Doktor', 'Doçent Doktor', 'Profesör Doktor', 'Ordinaryüs Profesör'],
	v: ['v3', 'v6', 'v8', 'v10', 'v12']
};

let cachedUsers = [];
let allRawTransactions = [];
let myUserTransactions = [];
let currentUser = null;
let currentTimeFilter = 'all';
let devopsUser = null;
let currentIteration = null;

const escapeHtml = s => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function extractEmail(str) {
	if (!str) return '';
	const match = str.match(/<([^>]+)>/);
	if (match) return match[1].trim().toLowerCase();
	if (str.includes('@')) return str.trim().toLowerCase();
	return '';
}

function cleanDisplayName(str) {
	if (!str) return '';
	return str.replace(/<[^>]+>/, '').trim();
}

function maskName(name) {
	if (!name) return '***';
	const clean = cleanDisplayName(name);
	return clean.split(' ').filter(Boolean).map(part => (part.length <= 1 ? part + '***' : part[0] + '***')).join(' ');
}

// Collapsible
let detailsOpen = false;
function toggleDetails() {
	detailsOpen = !detailsOpen;
	document.getElementById('detailsPanel').hidden = !detailsOpen;
	document.getElementById('detailsToggleLabel').innerText = detailsOpen ? 'Detayları gizle' : 'Detayları göster';
	document.getElementById('detailsArrow').innerHTML = detailsOpen ? '&#9650;' : '&#9660;';
}


// Check admin membership from the backend
async function checkAdminStatus() {
	let token = '';
	if (window.SDK && typeof SDK.getAccessToken === 'function') {
		try {
			token = await Promise.race([
				SDK.getAccessToken(),
				new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 1000))
			]);
		} catch (e) {
			token = '';
		}
	}
	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	try {
		const res = await fetch(`${API_BASE}/api/admin/check`, { headers });
		if (!res.ok) return;
		const data = await res.json();

		const adminBtn = document.getElementById('btnAdmin');
		if (adminBtn) {
			adminBtn.style.display = data.isAdmin ? 'inline-flex' : 'none';
		}
	} catch (e) {
		console.error("Admin yetki kontrolü başarısız:", e);
	}
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
// Subtabs
const subtabs = ['subtab-gecmis', 'subtab-istatistik', 'subtab-reddedilen', 'subtab-yardim'];
function setSubTab(idx, btn) {
	document.querySelectorAll('.sub-tab-btn').forEach((b, i) => b.classList.toggle('active', i === idx));
	subtabs.forEach((id, i) => {
		const el = document.getElementById(id);
		if (i === idx) {
			el.removeAttribute('hidden');
		} else {
			el.setAttribute('hidden', '');
		}
	});
}
// Time Filtering Logic
async function applyTimeScope(scope, btn) {
	currentTimeFilter = scope;
	btn.parentElement.querySelectorAll('.pill-btn').forEach(b => b.classList.remove('active'));
	btn.classList.add('active');

	document.getElementById('myScoreSubLabel').innerText = `TÜM ŞİRKET \u2022 ${btn.innerText.toUpperCase()}`;
	document.getElementById('leaderFilterTag').innerText = btn.innerText;

	let token = '';
	if (window.SDK && typeof SDK.getAccessToken === 'function') {
		try {
			token = await Promise.race([
				SDK.getAccessToken(),
				new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 1000))
			]);
		} catch (e) {
			token = '';
		}
	}

	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};
	const lbRes = await fetch(`${API_BASE}/api/leaderboard?scope=${scope}&page=1&pageSize=100`, { headers });
	const lbData = await lbRes.json();
	cachedUsers = lbData.items || [];

	renderDynamicViews();
}

function isTransactionInScope(txDate, scope, iteration) {
	if (scope === 'all') return true;
	const now = new Date();
	const t = new Date(txDate);

	if (scope === 'today') {
		return t.toDateString() === now.toDateString();
	}
	if (scope === '7days') {
		const diffDays = (now - t) / (1000 * 60 * 60 * 24);
		return diffDays >= 0 && diffDays <= 7;
	}
	if (scope === 'sprint') {
		return iteration === currentIteration;
	}
	return true;
}

// Fetching and matching user
async function refreshAll() {
	try {
		await loadAllData();
	} catch (e) {
		console.error("Yükleme hatası:", e);
	}
}

function resolveCurrentUser(users) {
	if (!users || !users.length) return null;

	// 1. In Program.cs, only the calling user has their unmasked name (everyone else has '***')
	const unmasked = users.find(u => u.userName && !u.userName.includes('***'));
	if (unmasked) return unmasked;

	// 2. Fall back to DevOps SDK context if available
	if (devopsUser) {
		const targetEmail = extractEmail(devopsUser.email || devopsUser.name || devopsUser.uniqueName || '');
		const targetDisplayName = cleanDisplayName(devopsUser.displayName || devopsUser.name || '').toLowerCase();

		if (targetEmail) {
			const byEmail = users.find(u => extractEmail(u.userName) === targetEmail);
			if (byEmail) return byEmail;
		}

		if (targetDisplayName) {
			const byName = users.find(u => cleanDisplayName(u.userName).toLowerCase() === targetDisplayName);
			if (byName) return byName;

			// Handle masked matching by initials
			const targetParts = targetDisplayName.split(' ').filter(Boolean);
			const byInitials = users.find(u => {
				const parts = cleanDisplayName(u.userName).toLowerCase().split(' ').filter(Boolean);
				if (parts.length !== targetParts.length) return false;
				return parts.every((p, idx) => p[0] === targetParts[idx][0]);
			});
			if (byInitials) return byInitials;
		}
	}

	return users[0] || null;
}

async function loadAllData() {
	let token = '';
	if (window.SDK && typeof SDK.getAccessToken === 'function') {
		try {
			token = await Promise.race([
				SDK.getAccessToken(),
				new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 1000))
			]);
		} catch (e) {
			token = '';
		}
	}

	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	const [lbRes, txRes, itRes] = await Promise.all([
		fetch(`${API_BASE}/api/leaderboard?page=1&pageSize=100`, { headers }),
		fetch(`${API_BASE}/api/transactions?page=1&pageSize=500`, { headers }),
		fetch(`${API_BASE}/api/currentiteration`, { headers })
	]);

	const lbData = await lbRes.json();
	const itData = await itRes.json();
	const txData = await txRes.json();

	cachedUsers = lbData.items || [];
	allRawTransactions = txData.items || [];
	currentIteration = itData.iterationPath || null;
	const headIterEl = document.getElementById('headIterationLabel');
	if (headIterEl) headIterEl.innerText =currentIteration;

	const footerIterEl = document.getElementById('footerIterationLabel');
	if (footerIterEl) footerIterEl.innerText = `${currentIteration} (2 Hafta)`;

	document.getElementById('leaderTotalUsers').innerText = `${lbData.totalCount || cachedUsers.length} kişi`;

	if (!cachedUsers.length) return;

	currentUser = resolveCurrentUser(cachedUsers);
	const currentEmail = currentUser ? extractEmail(currentUser.userName) : '';

	myUserTransactions = allRawTransactions.filter(t => {
		if (!currentUser) return false;
		if (t.userName === currentUser.userName) return true;
		const txEmail = extractEmail(t.userName);
		return currentEmail && txEmail && txEmail === currentEmail;
	});

	renderDynamicViews();
	if (currentUser) renderRozetlerGrid(currentUser);
	checkAdminStatus();
}

function renderDynamicViews() {
	if (!currentUser) return;

	const dynamicRankedUsers = cachedUsers.map(u => ({
		...u,
		filteredPoints: u.points
	})).sort((a, b) => b.filteredPoints - a.filteredPoints || a.userName.localeCompare(b.userName));

	const myRankIndex = dynamicRankedUsers.findIndex(u => u.id === currentUser.id);
	const myRank = myRankIndex !== -1 ? myRankIndex + 1 : 1;
	const myUserObj = dynamicRankedUsers.find(u => u.id === currentUser.id);
	const myCurrentScore = myUserObj ? myUserObj.filteredPoints : 0;

	document.getElementById('myRankBadge').innerText = `#${myRank}`;
	document.getElementById('myUserName').innerText = cleanDisplayName(currentUser.userName);
	document.getElementById('myFilteredPoints').innerText = myCurrentScore;
	document.getElementById('footerSprintRank').innerText = `${myCurrentScore}p \u2022 #${myRank}`;

	const todayStr = new Date().toDateString();
	const todayPoints = myUserTransactions
		.filter(t => new Date(t.timestamp).toDateString() === todayStr)
		.reduce((acc, t) => acc + (t.deltaPoints || 0), 0);
	document.getElementById('myDailySummary').innerText = `Bugün ${todayPoints}p \u2022`;

	renderLeaderboardColumn(dynamicRankedUsers, currentUser.id);
	renderGecmisTab();
	renderIstatistikTab();
	renderReddedilenTab();
}

function renderLeaderboardColumn(users, myId) {
	const top3 = users.slice(0, 3);
	const bottomStartIndex = Math.max(3, users.length - 3);
	const bottom3 = users.length > 3 ? users.slice(bottomStartIndex) : [];

	const isUserMe = u => Boolean((myId != null && u.id === myId) || (currentUser && u.userName === currentUser.userName));
	const myIndex = users.findIndex(isUserMe);

	const isInTop3 = myIndex !== -1 && myIndex < 3;
	const isInBottom3 = myIndex !== -1 && myIndex >= bottomStartIndex;
	const isInMiddle = myIndex !== -1 && !isInTop3 && !isInBottom3;

	const renderRow = (u, rank, isMe) => {
		const medalIcon = rank === 1 ? '&#129351;' : rank === 2 ? '&#129352;' : rank === 3 ? '&#129353;' : rank;
		const cleanName = cleanDisplayName(u.userName);
		const displayName = isMe
			? `${escapeHtml(cleanName)} <span class="tag-me">SEN</span>`
			: (u.userName ? maskName(u.userName) : `Kullanıcı #${rank}`);
		const sub = isMe ? `<span style="font-size:0.72rem; color:var(--muted); display:block; margin-top:2px;">Yazılım Geliştirme &bull; AlbarakaTech</span>` : '';

		return `
			<div class="leader-row ${isMe ? 'me' : ''}" onclick="${u.userName ? `openModal(${u.id})` : ''}">
				<div class="leader-left">
					<span class="leader-rank">${medalIcon}</span>
					<div>
						<span class="leader-name">${displayName}</span>
						${sub}
					</div>
				</div>
				<span class="leader-pts">${u.filteredPoints}p</span>
			</div>
		`;
	};

	// 1. Render Top 3
	document.getElementById('topRankersList').innerHTML = top3.map((u, i) => renderRow(u, i + 1, isUserMe(u))).join('');

	// 2. Middle Section: visible only when isInMiddle is true
	const middleSection = document.getElementById('middleRankSection');
	const middleEl = document.getElementById('myRankRowContainer');
	const allDots = document.querySelectorAll('.divider-dots');

	if (isInMiddle) {
		if (middleSection) middleSection.style.display = 'block';
		if (middleEl) middleEl.innerHTML = renderRow(users[myIndex], myIndex + 1, true);
		allDots.forEach(d => { d.style.display = 'block'; });
	} else {
		if (middleSection) middleSection.style.display = 'none';
		if (middleEl) middleEl.innerHTML = '';
		allDots.forEach(d => { d.style.display = 'none'; });
	}

	// 3. Render Bottom 3
	const bottomSection = document.getElementById('bottomRankersSection');
	if (bottom3.length > 0) {
		if (bottomSection) bottomSection.style.display = 'block';
		document.getElementById('bottomRankersList').innerHTML = bottom3.map((u, i) => {
			const rank = bottomStartIndex + i + 1;
			return renderRow(u, rank, isUserMe(u));
		}).join('');
	} else {
		if (bottomSection) bottomSection.style.display = 'none';
		document.getElementById('bottomRankersList').innerHTML = '';
	}
}

function renderGecmisTab() {
	const container = document.getElementById('subtab-gecmis');
	const filtered = myUserTransactions.filter(t => isTransactionInScope(t.timestamp, currentTimeFilter, t.iterationPath));

	if (!filtered.length) {
		container.innerHTML = '<p style="color:var(--muted); font-size:0.85rem; padding:10px;">Bu zaman aralığında işlem bulunmuyor.</p>';
		return;
	}

	container.innerHTML = filtered.map(t => {
		const sign = t.deltaPoints > 0 ? '+' : '';
		const ptCls = t.deltaPoints > 0 ? 'act-pos' : t.deltaPoints < 0 ? 'act-neg' : 'act-zero';

		return `
			<div class="activity-card">
				<div class="act-head">
					<span class="${ptCls}">${sign}${t.deltaPoints}p</span>
					<span>${escapeHtml(t.type || 'Puan Hareketi')}</span>
				</div>
				<div class="act-body">${escapeHtml(t.description || '')}</div>
				<div class="act-meta">#${t.workItemId || '---'} &bull; ${new Date(t.timestamp).toLocaleDateString('tr-TR')} ${new Date(t.timestamp).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })}</div>
			</div>
		`;
	}).join('');
}

function renderIstatistikTab() {
	const filtered = myUserTransactions.filter(t => isTransactionInScope(t.timestamp, currentTimeFilter, t.iterationPath));
	const totalPts = filtered.reduce((acc, t) => acc + (t.deltaPoints || 0), 0);
	const commentCount = filtered.filter(t => t.type === 'Comment Added' && t.deltaPoints > 0).length;
	const completedWorkPoints = filtered.filter(t => t.type === 'Completed Work Updated').reduce((acc, t) => acc + (t.deltaPoints || 0), 0);
	const remainingWorkPoints = filtered.filter(t => t.type === 'Remaining Work Updated').reduce((acc, t) => acc + (t.deltaPoints || 0), 0);
	const closedTaskCount = filtered.filter(t => t.type === 'State Changed' && t.deltaPoints > 0).length;

	document.getElementById('statsGrid').innerHTML = `
		<div class="stat-box">
			<div class="stat-val">${totalPts}</div>
			<div class="stat-lbl">Kazanılan Puan</div>
		</div>
		<div class="stat-box">
			<div class="stat-val">${commentCount}</div>
			<div class="stat-lbl">Anlamlı Yorum Sayısı</div>
		</div>
		<div class="stat-box">
			<div class="stat-val">${completedWorkPoints}p</div>
			<div class="stat-lbl">Completed Work Puanı</div>
		</div>
		<div class="stat-box">
			<div class="stat-val">${remainingWorkPoints}p</div>
			<div class="stat-lbl">Remaining Work Puanı</div>
		</div>
		<div class="stat-box">
			<div class="stat-val">${closedTaskCount}</div>
			<div class="stat-lbl">Kapatılan Görev</div>
		</div>
		<div class="stat-box">
			<div class="stat-val">${filtered.length}</div>
			<div class="stat-lbl">Toplam İşlem Logu</div>
		</div>
	`;
}

function renderReddedilenTab() {
	const container = document.getElementById('subtab-reddedilen');
	const rejected = myUserTransactions.filter(t => (t.deltaPoints || 0) <= 0);

	if (!rejected.length) {
		container.innerHTML = '<p style="color:var(--muted); font-size:0.85rem; padding:10px;">Reddedilen veya 0 puanlı bir hareket bulunmuyor.</p>';
		return;
	}

	container.innerHTML = rejected.map(t => {
		const ptCls = t.deltaPoints < 0 ? 'act-neg' : 'act-zero';
		return `
			<div class="activity-card">
				<div class="act-head">
					<span class="${ptCls}">${t.deltaPoints}p</span>
					<span>${escapeHtml(t.type || 'Puan Verilmedi')}</span>
				</div>
				<div class="act-body">${escapeHtml(t.description || 'Puan kriterlerini karşılamadı')}</div>
				<div class="act-meta">#${t.workItemId || '---'} &bull; ${new Date(t.timestamp).toLocaleDateString('tr-TR')}</div>
			</div>
		`;
	}).join('');
}

function renderRozetlerGrid(user) {
	const grid = document.getElementById('rozetGrid');
	const medals = user.medals || [];

	let html = `
		<div class="rozet-box" onclick="openCurrentUserModal()">
			<div class="rozet-icon">&#127919;</div>
			<div class="rozet-name">İlk Adım</div>
			<div class="rozet-desc">İlk puanını kazandın!</div>
		</div>
	`;

	medals.slice(0, 3).forEach(m => {
		let icon = '&#127942;';
		if (RANKS.mil.includes(m.type)) icon = '&#9876;';
		if (RANKS.acad.includes(m.type)) icon = '&#127891;';
		html += `
			<div class="rozet-box" onclick="openCurrentUserModal()">
				<div class="rozet-icon">${icon}</div>
				<div class="rozet-name">${escapeHtml(m.type)}</div>
				<div class="rozet-desc">${escapeHtml(m.description || 'Kazanıldı')}</div>
			</div>
		`;
	});

	grid.innerHTML = html;
}

function openCurrentUserModal() {
	if (currentUser) openModal(currentUser.id);
}

function openModal(id) {
	const u = cachedUsers.find(x => x.id === id);
	if (!u) return;

	document.getElementById('mUser').innerText = cleanDisplayName(u.userName);
	document.getElementById('mPts').innerText = `${u.points} Points`;
	const list = u.medals || [];

	const dailyCounts = list.reduce((acc, m) => {
		if (m.type === 'Daily Warrior' || m.type === 'Daily Commenter') acc[m.type] = (acc[m.type] || 0) + 1;
		return acc;
	}, {});

	const segs = [
		{ t: '⚔️ Askeri Rütbeler (Warrior)', items: list.filter(m => RANKS.mil.includes(m.type)) },
		{ t: '🎓 Akademik Başarılar (Commenter)', items: list.filter(m => RANKS.acad.includes(m.type)) },
		{ t: '🏆 Süreklilik (V-Serisi)', items: list.filter(m => RANKS.v.includes(m.type)) }
	];

	let html = segs.filter(s => s.items.length).map(s => `
		<div style="margin-bottom:14px;">
			<div class="seg-title">${s.t}</div>
			${s.items.map(m => `<div class="medal-item"><strong>${escapeHtml(m.type)}</strong> <span>${escapeHtml(m.description)}</span></div>`).join('')}
		</div>`).join('');

	if (Object.keys(dailyCounts).length) {
		html += `<div style="margin-bottom:14px;"><div class="seg-title">📜 Günlük Şampiyonluklar</div><div style="display:flex; gap:6px; flex-wrap:wrap;">` +
			Object.entries(dailyCounts).map(([type, cnt]) => `
				<span style="background:rgba(51,65,85,0.6); color:#cbd5e1; border:1px solid var(--border); border-radius:6px; font-size:0.7rem; font-weight:700; padding:2px 7px;">${type === 'Daily Warrior' ? '🗡️' : '💬'} ${type} &times;${cnt}</span>
			`).join('') + `</div></div>`;
	}

	document.getElementById('mList').innerHTML = html || '<p style="color:var(--muted);text-align:center;">Henüz kazanılmış bir madalya yok.</p>';
	document.getElementById('userModal').removeAttribute('hidden');
}

function closeModal() { document.getElementById('userModal').setAttribute('hidden', ''); }
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

// SDK Init: Read host identity
if (window.SDK && typeof SDK.init === 'function') {
	SDK.init({ applyTheme: true });
	const readyTimeout = new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), 1000));

	Promise.race([SDK.ready(), readyTimeout])
		.then(() => {
			try {
				devopsUser = SDK.getUser();
			} catch (e) {
				devopsUser = null;
			}
			SDK.notifyLoadSucceeded();
			refreshAll();
		})
		.catch(() => {
			devopsUser = null;
			refreshAll();
		});
	checkAdminStatus();
} else {
	devopsUser = null;
	refreshAll();
}
