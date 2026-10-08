/** The operator's address as a mail link, or nothing when the deployment set none. */
export default function Contact({ email }: { email: string | null }) {
  if (email === null) return null
  return <a href={`mailto:${email}`}>{email}</a>
}
