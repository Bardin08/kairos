import { useParams } from 'wouter'

// Placeholder. The manage-a-booking page is built here.
export default function Manage() {
  const { id } = useParams()
  return <main>{id}</main>
}
