function formatOpenSslDateCentral(value) {
  if (!value) return '';

  // OpenSSL commonly returns dates like: May 27 09:53:09 2026 GMT
  const match = value.trim().match(/^([A-Za-z]{3})\s+(\d{1,2})\s+(\d{2}:\d{2}:\d{2})\s+(\d{4})\s+GMT$/);
  if (!match) return value;

  const months = {
    Jan: 0, Feb: 1, Mar: 2, Apr: 3, May: 4, Jun: 5,
    Jul: 6, Aug: 7, Sep: 8, Oct: 9, Nov: 10, Dec: 11
  };

  const [, monthName, dayText, timeText, yearText] = match;
  const month = months[monthName];
  if (month === undefined) return value;

  const [hourText, minuteText, secondText] = timeText.split(':');
  const date = new Date(Date.UTC(
    Number(yearText),
    month,
    Number(dayText),
    Number(hourText),
    Number(minuteText),
    Number(secondText)
  ));

  const datePart = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    month: '2-digit',
    day: '2-digit',
    year: 'numeric'
  }).format(date);

  const timePart = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
    hour12: true
  }).format(date);

  return `${datePart}, ${timePart}`;
}

async function loadCertificateStatus() {
  const serialEl = document.getElementById('cert-serial');
  const validityEl = document.getElementById('cert-validity');

  try {
    let response = await fetch(`/cgi-bin/cert-info.cgi?_=${Date.now()}`, { cache: 'no-store' });
    if (!response.ok) {
      response = await fetch(`cert-info.json?_=${Date.now()}`, { cache: 'no-store' });
    }
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const cert = await response.json();
    if (cert.error) throw new Error(cert.error);

    serialEl.textContent = cert.serialNumber || 'unavailable';

    if (cert.notBefore && cert.notAfter) {
      validityEl.innerHTML = `Not before ${formatOpenSslDateCentral(cert.notBefore)} central and<br>Not after ${formatOpenSslDateCentral(cert.notAfter)} central`;
    } else {
      validityEl.textContent = 'unavailable';
    }
  } catch (error) {
    serialEl.textContent = 'unavailable';
    validityEl.textContent = 'unable to read cert-info.json';
    console.error('Unable to load certificate status:', error);
  }
}

loadCertificateStatus();
setInterval(loadCertificateStatus, 60000);
