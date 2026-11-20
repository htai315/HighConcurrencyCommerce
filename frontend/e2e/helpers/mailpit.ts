/**
 * Helper to query Mailpit HTTP API during live E2E tests.
 */
const MAILPIT_URL = process.env.MAILPIT_URL || 'http://localhost:8025';

export interface MailpitMessageSummary {
  ID: string;
  Subject: string;
  To: { Name: string; Address: string }[];
  From: { Name: string; Address: string };
  Created: string;
}

export interface MailpitMessageDetail {
  ID: string;
  Subject: string;
  Text: string;
  HTML: string;
}

export async function clearAllEmails(): Promise<void> {
  try {
    await fetch(`${MAILPIT_URL}/api/v1/messages`, { method: 'DELETE' });
  } catch (err) {
    console.warn('Mailpit clear failed (Mailpit might not be running locally):', err);
  }
}

export interface WaitForEmailOptions {
  subject?: string;
  timeoutMs?: number;
}

export async function listEmails(
  recipientEmail: string,
  options: Pick<WaitForEmailOptions, 'subject'> = {}
): Promise<MailpitMessageSummary[]> {
  const normalizedTarget = recipientEmail.trim().toLowerCase();
  const expectedSubject = options.subject?.trim().toLowerCase();
  const response = await fetch(`${MAILPIT_URL}/api/v1/messages`);
  if (!response.ok) {
    throw new Error(`Mailpit returned HTTP ${response.status} while listing messages.`);
  }

  const data = await response.json() as { messages?: MailpitMessageSummary[] };
  return (data.messages ?? []).filter(message => {
    const recipientMatch = message.To.some(recipient => recipient.Address.toLowerCase() === normalizedTarget);
    if (!recipientMatch) return false;
    return !expectedSubject || message.Subject.toLowerCase().includes(expectedSubject);
  });
}

export async function waitForEmailCount(
  recipientEmail: string,
  expectedCount: number,
  options: WaitForEmailOptions = {}
): Promise<MailpitMessageSummary[]> {
  const timeoutMs = options.timeoutMs ?? 15_000;
  const startedAt = Date.now();
  let matches: MailpitMessageSummary[] = [];

  while (Date.now() - startedAt < timeoutMs) {
    try {
      matches = await listEmails(recipientEmail, { subject: options.subject });
      if (matches.length === expectedCount) return matches;
    } catch {
      // Mailpit can be briefly unavailable while the Docker demo stack is starting.
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }

  return matches;
}

export async function waitForEmail(
  recipientEmail: string,
  optionsOrTimeout: WaitForEmailOptions | number = 15_000
): Promise<MailpitMessageDetail | null> {
  const options: WaitForEmailOptions = typeof optionsOrTimeout === 'number'
    ? { timeoutMs: optionsOrTimeout }
    : optionsOrTimeout;
  const timeoutMs = options.timeoutMs ?? 15_000;
  const expectedSubject = options.subject?.toLowerCase();
  const startTime = Date.now();
  const normalizedTarget = recipientEmail.trim().toLowerCase();

  while (Date.now() - startTime < timeoutMs) {
    try {
      const res = await fetch(`${MAILPIT_URL}/api/v1/messages`);
      if (res.ok) {
        const data = await res.json() as { messages: MailpitMessageSummary[] };
        const found = data.messages?.find(m => {
          const recipientMatch = m.To.some(t => t.Address.toLowerCase() === normalizedTarget);
          if (!recipientMatch) return false;
          if (expectedSubject && !m.Subject.toLowerCase().includes(expectedSubject)) return false;
          return true;
        });
        if (found) {
          const detailRes = await fetch(`${MAILPIT_URL}/api/v1/message/${found.ID}`);
          if (detailRes.ok) {
            return (await detailRes.json()) as MailpitMessageDetail;
          }
        }
      }
    } catch {
      // transient connection error, retry
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  return null;
}

export function extractFragmentTokenFromText(content: string): string | null {
  // Look for #token=... in links
  const match = content.match(/#token=([a-zA-Z0-9_-]+)/);
  return match ? match[1] : null;
}

