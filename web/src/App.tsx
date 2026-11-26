import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { LiveMap } from './map/LiveMap'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
    },
  },
})

/** App shell: TanStack Query + live map with stop/route panels (stage 3.7). */
export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <LiveMap />
    </QueryClientProvider>
  )
}
