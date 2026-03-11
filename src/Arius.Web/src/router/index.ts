import { createRouter, createWebHistory } from 'vue-router'
import MainView from '@/views/MainView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      name: 'home',
      component: MainView,
    },
    {
      path: '/:snapshotId',
      name: 'snapshot',
      component: MainView,
      props: true,
    },
    {
      path: '/:snapshotId/tree/:pathMatch(.*)*',
      name: 'tree',
      component: MainView,
      props: true,
    },
  ],
})
