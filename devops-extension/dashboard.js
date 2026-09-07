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

const escapeHtml = s => String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');

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
function applyTimeScope(scope, btn) {
	currentTimeFilter = scope;
	btn.parentElement.querySelectorAll('.pill-btn').forEach(b => b.classList.remove('active'));
	btn.classList.add('active');

	document.getElementById('myScoreSubLabel').innerText = `TÜM ŞİRKET \u2022 ${btn.innerText.toUpperCase()}`;
	document.getElementById('leaderFilterTag').innerText = btn.innerText;

	renderDynamicViews();
}

function isTransactionInScope(txDate, scope) {
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
		const diffDays = (now - t) / (1000 * 60 * 60 * 24);
		return diffDays >= 0 && diffDays <= 14;
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
	const caller = users.find(u => Array.isArray(u.medals));
	if (caller) return caller;

	if (devopsUser) {
		const devopsEmail = extractEmail(devopsUser.name || devopsUser.email || devopsUser.uniqueName || '');
		const devopsDisplayName = cleanDisplayName(devopsUser.displayName || devopsUser.name || '').toLowerCase();

		if (devopsEmail) {
			const matchedByEmail = users.find(u => extractEmail(u.userName) === devopsEmail);
			if (matchedByEmail) return matchedByEmail;
		}

		if (devopsDisplayName) {
			const matchedByName = users.find(u => cleanDisplayName(u.userName).toLowerCase() === devopsDisplayName);
			if (matchedByName) return matchedByName;
		}
	}
	return users[0];
}
async function loadAllData() {
	let token = '';
	if (window.SDK && typeof SDK.getAccessToken === 'function') {
		try {
			token = await SDK.getAccessToken();
		} catch (e) {
			console.warn("Token alınamadı:", e);
		}
	}

	const headers = token ? { 'Authorization': `Bearer ${token}` } : {};

	const [lbRes, txRes] = await Promise.all([
		fetch(`${API_BASE}/api/leaderboard?page=1&pageSize=100`, { headers }),
		fetch(`${API_BASE}/api/transactions?page=1&pageSize=500`, { headers })
	]);

	const lbData = await lbRes.json();
	const txData = await txRes.json();

	cachedUsers = lbData.items || [];
	allRawTransactions = txData.items || [];

	document.getElementById('leaderTotalUsers').innerText = `${lbData.totalCount || cachedUsers.length} kişi`;

	if (!cachedUsers.length) return;

	currentUser = resolveCurrentUser(cachedUsers);
	const currentEmail = extractEmail(currentUser.userName);

	myUserTransactions = allRawTransactions.filter(t => {
		if (t.userName === currentUser.userName) return true;
		const txEmail = extractEmail(t.userName);
		return currentEmail && txEmail && txEmail === currentEmail;
	});

	renderDynamicViews();
	renderRozetlerGrid(currentUser);
}

function resolveCurrentUser(users) {
	if (devopsUser) {
		// Extract email from SDK context (e.g. devopsUser.name = "John Doe <john@doe.com>")
		const devopsEmail = extractEmail(devopsUser.name || devopsUser.email || devopsUser.uniqueName || '');
		const devopsDisplayName = cleanDisplayName(devopsUser.displayName || devopsUser.name || '').toLowerCase();

		if (devopsEmail) {
			const matchedByEmail = users.find(u => extractEmail(u.userName) === devopsEmail);
			if (matchedByEmail) return matchedByEmail;
		}

		if (devopsDisplayName) {
			const matchedByName = users.find(u => cleanDisplayName(u.userName).toLowerCase() === devopsDisplayName);
			if (matchedByName) return matchedByName;
		}
	}
	return users[0];
}

function renderDynamicViews() {
	if (!currentUser) return;

	const userScoreMap = {};
	cachedUsers.forEach(u => {
		userScoreMap[u.userName] = (currentTimeFilter === 'all') ? u.points : 0;
	});

	if (currentTimeFilter !== 'all') {
		allRawTransactions.forEach(t => {
			if (isTransactionInScope(t.timestamp, currentTimeFilter)) {
				const matchedUser = cachedUsers.find(u => {
					if (u.userName === t.userName) return true;
					const uEmail = extractEmail(u.userName);
					const tEmail = extractEmail(t.userName);
					return uEmail && tEmail && uEmail === tEmail;
				});
				const key = matchedUser ? matchedUser.userName : t.userName;
				userScoreMap[key] = (userScoreMap[key] || 0) + (t.deltaPoints || 0);
			}
		});
	}

	const dynamicRankedUsers = cachedUsers.map(u => ({
		...u,
		filteredPoints: userScoreMap[u.userName] || 0
	})).sort((a, b) => b.filteredPoints - a.filteredPoints || a.userName.localeCompare(b.userName));

	const myRankIndex = dynamicRankedUsers.findIndex(u => u.id === currentUser.id);
	const myRank = myRankIndex !== -1 ? myRankIndex + 1 : 1;
	const myCurrentScore = userScoreMap[currentUser.userName] || 0;

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
	const top5 = users.slice(0, 5);
	const bottom5 = users.length > 5 ? users.slice(-5) : [];

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

	document.getElementById('topRankersList').innerHTML = top5.map((u, i) => renderRow(u, i + 1, u.id === myId)).join('');

	const myIndex = users.findIndex(u => u.id === myId);
	if (myIndex !== -1) {
		document.getElementById('myRankRowContainer').innerHTML = renderRow(users[myIndex], myIndex + 1, true);
	}

	document.getElementById('bottomRankersList').innerHTML = bottom5.map((u, i) => {
		const rank = users.length - bottom5.length + i + 1;
		return renderRow(u, rank, u.id === myId);
	}).join('');
}

function renderGecmisTab() {
	const container = document.getElementById('subtab-gecmis');
	const filtered = myUserTransactions.filter(t => isTransactionInScope(t.timestamp, currentTimeFilter));

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
				<div class="act-meta">#${t.workItemId || '---'} &bull; ${new Date(t.timestamp).toLocaleDateString('tr-TR')} ${new Date(t.timestamp).toLocaleTimeString('tr-TR', {hour:'2-digit', minute:'2-digit'})}</div>
			</div>
		`;
	}).join('');
}

function renderIstatistikTab() {
	const filtered = myUserTransactions.filter(t => isTransactionInScope(t.timestamp, currentTimeFilter));
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
if (window.SDK) {
	SDK.init({ applyTheme: true });
	SDK.ready().then(() => {
		try {
			devopsUser = SDK.getUser();
		} catch (e) {
			console.warn("Could not retrieve Azure DevOps user context:", e);
		}
		SDK.notifyLoadSucceeded();
		refreshAll();
	});
} else {
	refreshAll();
}
