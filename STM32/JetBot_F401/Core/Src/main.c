/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.c
  * @brief          : Main program body
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */
/* Includes ------------------------------------------------------------------*/
#include "main.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */

/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */

/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/
TIM_HandleTypeDef htim1;
TIM_HandleTypeDef htim3;
TIM_HandleTypeDef htim4;

UART_HandleTypeDef huart2;

/* USER CODE BEGIN PV */

#define RX_BUF_SIZE        64
#define DRIVE_WATCHDOG_MS  300U

static uint8_t rx_byte;

static char rx_build[RX_BUF_SIZE];
static char rx_line[RX_BUF_SIZE];

static volatile uint16_t rx_index = 0;
static volatile uint8_t line_ready = 0;

static int16_t left_applied = 0;
static int16_t right_applied = 0;

static uint32_t last_drive_tick = 0;

/* USER CODE END PV */

/* Private function prototypes -----------------------------------------------*/
void SystemClock_Config(void);
static void MX_GPIO_Init(void);
static void MX_TIM1_Init(void);
static void MX_TIM3_Init(void);
static void MX_TIM4_Init(void);
static void MX_USART2_UART_Init(void);
/* USER CODE BEGIN PFP */
static void UART_Send(
    const char *text);

static void Drive_Stop(void);

static void Motor_SetSafe(
    GPIO_TypeDef *in1_port,
    uint16_t in1_pin,
    GPIO_TypeDef *in2_port,
    uint16_t in2_pin,
    TIM_HandleTypeDef *htim,
    uint32_t channel,
    int command,
    int16_t *last_command);

static void Drive_Apply(int left, int right);

static void Process_Command(const char *line);
/* USER CODE END PFP */

/* Private user code ---------------------------------------------------------*/
/* USER CODE BEGIN 0 */

/* USER CODE END 0 */

/**
  * @brief  The application entry point.
  * @retval int
  */
int main(void)
{

  /* USER CODE BEGIN 1 */

  /* USER CODE END 1 */

  /* MCU Configuration--------------------------------------------------------*/

  /* Reset of all peripherals, Initializes the Flash interface and the Systick. */
  HAL_Init();

  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* Configure the system clock */
  SystemClock_Config();

  /* USER CODE BEGIN SysInit */

  /* USER CODE END SysInit */

  /* Initialize all configured peripherals */
  MX_GPIO_Init();
  MX_TIM1_Init();
  MX_TIM3_Init();
  MX_TIM4_Init();
  MX_USART2_UART_Init();
  /* USER CODE BEGIN 2 */
  HAL_TIM_PWM_Start(&htim3, TIM_CHANNEL_1);
  HAL_TIM_PWM_Start(&htim3, TIM_CHANNEL_2);

  //HAL_TIM_PWM_Start(&htim4, TIM_CHANNEL_1);

  //HAL_TIM_PWM_Start(&htim1, TIM_CHANNEL_1);
  //HAL_TIM_PWM_Start(&htim1, TIM_CHANNEL_2);
  //HAL_TIM_PWM_Start(&htim1, TIM_CHANNEL_3);

  /* 모터를 확실하게 정지 상태로 시작 */
  Drive_Stop();

  /* watchdog 기준시간 초기화 */
  last_drive_tick = HAL_GetTick();

  /* Arm DC motor STOP */
  //__HAL_TIM_SET_COMPARE(&htim4, TIM_CHANNEL_1, 0);

  /* 360 servo STOP */
  //__HAL_TIM_SET_COMPARE(&htim1, TIM_CHANNEL_1, 1500);
  //__HAL_TIM_SET_COMPARE(&htim1, TIM_CHANNEL_2, 1500);

  /* 180 servo center */
  //__HAL_TIM_SET_COMPARE(&htim1, TIM_CHANNEL_3, 1500);

  HAL_UART_Receive_IT(&huart2, &rx_byte, 1);
  /* USER CODE END 2 */

  /* Infinite loop */
  /* USER CODE BEGIN WHILE */
  while (1)
  {
    /* USER CODE END WHILE */

    /* USER CODE BEGIN 3 */
	  /*
	   * 완성된 UART command가 있으면 처리
	   */
	  if (line_ready)
	  {
	      Process_Command(
	          rx_line);

	      line_ready = 0;
	  }


	  /*
	   * 마지막 정상 주행 명령 이후
	   * 300ms 이상 지났으면 강제 정지
	   */
	  if (
	      HAL_GetTick()
	      - last_drive_tick
	      > DRIVE_WATCHDOG_MS
	  )
	  {
	      if (
	          left_applied != 0
	          ||
	          right_applied != 0
	      )
	      {
	          Drive_Stop();
	      }
	  }

  }
  /* USER CODE END 3 */
}

/**
  * @brief System Clock Configuration
  * @retval None
  */
void SystemClock_Config(void)
{
  RCC_OscInitTypeDef RCC_OscInitStruct = {0};
  RCC_ClkInitTypeDef RCC_ClkInitStruct = {0};

  /** Configure the main internal regulator output voltage
  */
  __HAL_RCC_PWR_CLK_ENABLE();
  __HAL_PWR_VOLTAGESCALING_CONFIG(PWR_REGULATOR_VOLTAGE_SCALE2);

  /** Initializes the RCC Oscillators according to the specified parameters
  * in the RCC_OscInitTypeDef structure.
  */
  RCC_OscInitStruct.OscillatorType = RCC_OSCILLATORTYPE_HSI;
  RCC_OscInitStruct.HSIState = RCC_HSI_ON;
  RCC_OscInitStruct.HSICalibrationValue = RCC_HSICALIBRATION_DEFAULT;
  RCC_OscInitStruct.PLL.PLLState = RCC_PLL_ON;
  RCC_OscInitStruct.PLL.PLLSource = RCC_PLLSOURCE_HSI;
  RCC_OscInitStruct.PLL.PLLM = 16;
  RCC_OscInitStruct.PLL.PLLN = 336;
  RCC_OscInitStruct.PLL.PLLP = RCC_PLLP_DIV4;
  RCC_OscInitStruct.PLL.PLLQ = 7;
  if (HAL_RCC_OscConfig(&RCC_OscInitStruct) != HAL_OK)
  {
    Error_Handler();
  }

  /** Initializes the CPU, AHB and APB buses clocks
  */
  RCC_ClkInitStruct.ClockType = RCC_CLOCKTYPE_HCLK|RCC_CLOCKTYPE_SYSCLK
                              |RCC_CLOCKTYPE_PCLK1|RCC_CLOCKTYPE_PCLK2;
  RCC_ClkInitStruct.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
  RCC_ClkInitStruct.AHBCLKDivider = RCC_SYSCLK_DIV1;
  RCC_ClkInitStruct.APB1CLKDivider = RCC_HCLK_DIV2;
  RCC_ClkInitStruct.APB2CLKDivider = RCC_HCLK_DIV1;

  if (HAL_RCC_ClockConfig(&RCC_ClkInitStruct, FLASH_LATENCY_2) != HAL_OK)
  {
    Error_Handler();
  }
}

/**
  * @brief TIM1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM1_Init(void)
{

  /* USER CODE BEGIN TIM1_Init 0 */

  /* USER CODE END TIM1_Init 0 */

  TIM_MasterConfigTypeDef sMasterConfig = {0};
  TIM_OC_InitTypeDef sConfigOC = {0};
  TIM_BreakDeadTimeConfigTypeDef sBreakDeadTimeConfig = {0};

  /* USER CODE BEGIN TIM1_Init 1 */

  /* USER CODE END TIM1_Init 1 */
  htim1.Instance = TIM1;
  htim1.Init.Prescaler = 83;
  htim1.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim1.Init.Period = 19999;
  htim1.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim1.Init.RepetitionCounter = 0;
  htim1.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_DISABLE;
  if (HAL_TIM_PWM_Init(&htim1) != HAL_OK)
  {
    Error_Handler();
  }
  sMasterConfig.MasterOutputTrigger = TIM_TRGO_RESET;
  sMasterConfig.MasterSlaveMode = TIM_MASTERSLAVEMODE_DISABLE;
  if (HAL_TIMEx_MasterConfigSynchronization(&htim1, &sMasterConfig) != HAL_OK)
  {
    Error_Handler();
  }
  sConfigOC.OCMode = TIM_OCMODE_PWM1;
  sConfigOC.Pulse = 1500;
  sConfigOC.OCPolarity = TIM_OCPOLARITY_HIGH;
  sConfigOC.OCNPolarity = TIM_OCNPOLARITY_HIGH;
  sConfigOC.OCFastMode = TIM_OCFAST_DISABLE;
  sConfigOC.OCIdleState = TIM_OCIDLESTATE_RESET;
  sConfigOC.OCNIdleState = TIM_OCNIDLESTATE_RESET;
  if (HAL_TIM_PWM_ConfigChannel(&htim1, &sConfigOC, TIM_CHANNEL_1) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_TIM_PWM_ConfigChannel(&htim1, &sConfigOC, TIM_CHANNEL_2) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_TIM_PWM_ConfigChannel(&htim1, &sConfigOC, TIM_CHANNEL_3) != HAL_OK)
  {
    Error_Handler();
  }
  sBreakDeadTimeConfig.OffStateRunMode = TIM_OSSR_DISABLE;
  sBreakDeadTimeConfig.OffStateIDLEMode = TIM_OSSI_DISABLE;
  sBreakDeadTimeConfig.LockLevel = TIM_LOCKLEVEL_OFF;
  sBreakDeadTimeConfig.DeadTime = 0;
  sBreakDeadTimeConfig.BreakState = TIM_BREAK_DISABLE;
  sBreakDeadTimeConfig.BreakPolarity = TIM_BREAKPOLARITY_HIGH;
  sBreakDeadTimeConfig.AutomaticOutput = TIM_AUTOMATICOUTPUT_DISABLE;
  if (HAL_TIMEx_ConfigBreakDeadTime(&htim1, &sBreakDeadTimeConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM1_Init 2 */

  /* USER CODE END TIM1_Init 2 */
  HAL_TIM_MspPostInit(&htim1);

}

/**
  * @brief TIM3 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM3_Init(void)
{

  /* USER CODE BEGIN TIM3_Init 0 */

  /* USER CODE END TIM3_Init 0 */

  TIM_MasterConfigTypeDef sMasterConfig = {0};
  TIM_OC_InitTypeDef sConfigOC = {0};

  /* USER CODE BEGIN TIM3_Init 1 */

  /* USER CODE END TIM3_Init 1 */
  htim3.Instance = TIM3;
  htim3.Init.Prescaler = 83;
  htim3.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim3.Init.Period = 999;
  htim3.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim3.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_DISABLE;
  if (HAL_TIM_PWM_Init(&htim3) != HAL_OK)
  {
    Error_Handler();
  }
  sMasterConfig.MasterOutputTrigger = TIM_TRGO_RESET;
  sMasterConfig.MasterSlaveMode = TIM_MASTERSLAVEMODE_DISABLE;
  if (HAL_TIMEx_MasterConfigSynchronization(&htim3, &sMasterConfig) != HAL_OK)
  {
    Error_Handler();
  }
  sConfigOC.OCMode = TIM_OCMODE_PWM1;
  sConfigOC.Pulse = 0;
  sConfigOC.OCPolarity = TIM_OCPOLARITY_HIGH;
  sConfigOC.OCFastMode = TIM_OCFAST_DISABLE;
  if (HAL_TIM_PWM_ConfigChannel(&htim3, &sConfigOC, TIM_CHANNEL_1) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_TIM_PWM_ConfigChannel(&htim3, &sConfigOC, TIM_CHANNEL_2) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM3_Init 2 */

  /* USER CODE END TIM3_Init 2 */
  HAL_TIM_MspPostInit(&htim3);

}

/**
  * @brief TIM4 Initialization Function
  * @param None
  * @retval None
  */
static void MX_TIM4_Init(void)
{

  /* USER CODE BEGIN TIM4_Init 0 */

  /* USER CODE END TIM4_Init 0 */

  TIM_MasterConfigTypeDef sMasterConfig = {0};
  TIM_OC_InitTypeDef sConfigOC = {0};

  /* USER CODE BEGIN TIM4_Init 1 */

  /* USER CODE END TIM4_Init 1 */
  htim4.Instance = TIM4;
  htim4.Init.Prescaler = 83;
  htim4.Init.CounterMode = TIM_COUNTERMODE_UP;
  htim4.Init.Period = 999;
  htim4.Init.ClockDivision = TIM_CLOCKDIVISION_DIV1;
  htim4.Init.AutoReloadPreload = TIM_AUTORELOAD_PRELOAD_DISABLE;
  if (HAL_TIM_PWM_Init(&htim4) != HAL_OK)
  {
    Error_Handler();
  }
  sMasterConfig.MasterOutputTrigger = TIM_TRGO_RESET;
  sMasterConfig.MasterSlaveMode = TIM_MASTERSLAVEMODE_DISABLE;
  if (HAL_TIMEx_MasterConfigSynchronization(&htim4, &sMasterConfig) != HAL_OK)
  {
    Error_Handler();
  }
  sConfigOC.OCMode = TIM_OCMODE_PWM1;
  sConfigOC.Pulse = 0;
  sConfigOC.OCPolarity = TIM_OCPOLARITY_HIGH;
  sConfigOC.OCFastMode = TIM_OCFAST_DISABLE;
  if (HAL_TIM_PWM_ConfigChannel(&htim4, &sConfigOC, TIM_CHANNEL_1) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN TIM4_Init 2 */

  /* USER CODE END TIM4_Init 2 */
  HAL_TIM_MspPostInit(&htim4);

}

/**
  * @brief USART2 Initialization Function
  * @param None
  * @retval None
  */
static void MX_USART2_UART_Init(void)
{

  /* USER CODE BEGIN USART2_Init 0 */

  /* USER CODE END USART2_Init 0 */

  /* USER CODE BEGIN USART2_Init 1 */

  /* USER CODE END USART2_Init 1 */
  huart2.Instance = USART2;
  huart2.Init.BaudRate = 115200;
  huart2.Init.WordLength = UART_WORDLENGTH_8B;
  huart2.Init.StopBits = UART_STOPBITS_1;
  huart2.Init.Parity = UART_PARITY_NONE;
  huart2.Init.Mode = UART_MODE_TX_RX;
  huart2.Init.HwFlowCtl = UART_HWCONTROL_NONE;
  huart2.Init.OverSampling = UART_OVERSAMPLING_16;
  if (HAL_UART_Init(&huart2) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN USART2_Init 2 */

  /* USER CODE END USART2_Init 2 */

}

/**
  * @brief GPIO Initialization Function
  * @param None
  * @retval None
  */
static void MX_GPIO_Init(void)
{
  GPIO_InitTypeDef GPIO_InitStruct = {0};
  /* USER CODE BEGIN MX_GPIO_Init_1 */

  /* USER CODE END MX_GPIO_Init_1 */

  /* GPIO Ports Clock Enable */
  __HAL_RCC_GPIOC_CLK_ENABLE();
  __HAL_RCC_GPIOH_CLK_ENABLE();
  __HAL_RCC_GPIOA_CLK_ENABLE();
  __HAL_RCC_GPIOB_CLK_ENABLE();

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOA, TRACK_L_IN1_Pin|TRACK_L_IN2_Pin|TRACK_R_IN1_Pin|LD2_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(GPIOB, TRACK_R_IN2_Pin|ARM_DC_IN1_Pin|ARM_DC_IN2_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin : B1_Pin */
  GPIO_InitStruct.Pin = B1_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_IT_FALLING;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  HAL_GPIO_Init(B1_GPIO_Port, &GPIO_InitStruct);

  /*Configure GPIO pins : TRACK_L_IN1_Pin TRACK_L_IN2_Pin TRACK_R_IN1_Pin LD2_Pin */
  GPIO_InitStruct.Pin = TRACK_L_IN1_Pin|TRACK_L_IN2_Pin|TRACK_R_IN1_Pin|LD2_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOA, &GPIO_InitStruct);

  /*Configure GPIO pins : TRACK_R_IN2_Pin ARM_DC_IN1_Pin ARM_DC_IN2_Pin */
  GPIO_InitStruct.Pin = TRACK_R_IN2_Pin|ARM_DC_IN1_Pin|ARM_DC_IN2_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(GPIOB, &GPIO_InitStruct);

  /* USER CODE BEGIN MX_GPIO_Init_2 */

  /* USER CODE END MX_GPIO_Init_2 */
}

/* USER CODE BEGIN 4 */
static void Drive_Stop(void)
{
    __HAL_TIM_SET_COMPARE(
        &htim3,
        TIM_CHANNEL_1,
        0);

    __HAL_TIM_SET_COMPARE(
        &htim3,
        TIM_CHANNEL_2,
        0);

    HAL_GPIO_WritePin(
        TRACK_L_IN1_GPIO_Port,
        TRACK_L_IN1_Pin,
        GPIO_PIN_RESET);

    HAL_GPIO_WritePin(
        TRACK_L_IN2_GPIO_Port,
        TRACK_L_IN2_Pin,
        GPIO_PIN_RESET);

    HAL_GPIO_WritePin(
        TRACK_R_IN1_GPIO_Port,
        TRACK_R_IN1_Pin,
        GPIO_PIN_RESET);

    HAL_GPIO_WritePin(
        TRACK_R_IN2_GPIO_Port,
        TRACK_R_IN2_Pin,
        GPIO_PIN_RESET);

    left_applied = 0;
    right_applied = 0;
}

static void Motor_SetSafe(
    GPIO_TypeDef *in1_port,
    uint16_t in1_pin,
    GPIO_TypeDef *in2_port,
    uint16_t in2_pin,
    TIM_HandleTypeDef *htim,
    uint32_t channel,
    int command,
    int16_t *last_command)
{
    /* 범위 제한 */
    if (command > 1000)
        command = 1000;

    if (command < -1000)
        command = -1000;


    /*
     * 정회전 → 역회전
     * 또는
     * 역회전 → 정회전
     *
     * 즉시 반전하지 않고 한 control cycle 정지.
     */
    if (
        ((*last_command > 0) && (command < 0))
        ||
        ((*last_command < 0) && (command > 0))
    )
    {
        __HAL_TIM_SET_COMPARE(
            htim,
            channel,
            0);

        HAL_GPIO_WritePin(
            in1_port,
            in1_pin,
            GPIO_PIN_RESET);

        HAL_GPIO_WritePin(
            in2_port,
            in2_pin,
            GPIO_PIN_RESET);

        *last_command = 0;

        return;
    }


    /* 정지 */
    if (command == 0)
    {
        __HAL_TIM_SET_COMPARE(
            htim,
            channel,
            0);

        HAL_GPIO_WritePin(
            in1_port,
            in1_pin,
            GPIO_PIN_RESET);

        HAL_GPIO_WritePin(
            in2_port,
            in2_pin,
            GPIO_PIN_RESET);

        *last_command = 0;

        return;
    }


    /*
     * -1000~1000
     * →
     * PWM 0~999
     */
    uint32_t pwm =
        ((uint32_t)abs(command) * 999U)
        / 1000U;


    /* 정방향 */
    if (command > 0)
    {
        HAL_GPIO_WritePin(
            in1_port,
            in1_pin,
            GPIO_PIN_SET);

        HAL_GPIO_WritePin(
            in2_port,
            in2_pin,
            GPIO_PIN_RESET);
    }

    /* 역방향 */
    else
    {
        HAL_GPIO_WritePin(
            in1_port,
            in1_pin,
            GPIO_PIN_RESET);

        HAL_GPIO_WritePin(
            in2_port,
            in2_pin,
            GPIO_PIN_SET);
    }


    __HAL_TIM_SET_COMPARE(
        htim,
        channel,
        pwm);

    *last_command =
        (int16_t)command;
}

static void Drive_Apply(
    int left,
    int right)
{
    /* LEFT */
    Motor_SetSafe(
        TRACK_L_IN1_GPIO_Port,
        TRACK_L_IN1_Pin,
        TRACK_L_IN2_GPIO_Port,
        TRACK_L_IN2_Pin,
        &htim3,
        TIM_CHANNEL_1,
        left,
        &left_applied);


    /* RIGHT */
    Motor_SetSafe(
        TRACK_R_IN1_GPIO_Port,
        TRACK_R_IN1_Pin,
        TRACK_R_IN2_GPIO_Port,
        TRACK_R_IN2_Pin,
        &htim3,
        TIM_CHANNEL_2,
        right,
        &right_applied);
}

static void UART_Send(
    const char *text)
{
    HAL_UART_Transmit(
        &huart2,
        (uint8_t *)text,
        strlen(text),
        100);
}

static void Process_Command(
    const char *line)
{
    /*
     * STOP
     */
    if (strcmp(line, "STOP") == 0)
    {
        Drive_Stop();

        last_drive_tick =
            HAL_GetTick();

        UART_Send(
            "ACK,STOP\n");

        return;
    }


    unsigned long seq = 0;
    int left = 0;
    int right = 0;


    /*
     * 예상:
     *
     * M,123,200,200
     */
    int result = sscanf(
        line,
        "M,%lu,%d,%d",
        &seq,
        &left,
        &right);


    /* 형식 오류 */
    if (result != 3)
    {
        Drive_Stop();

        UART_Send(
            "ERR,FORMAT\n");

        return;
    }


    /* 명령 범위 오류 */
    if (
        left < -1000 ||
        left > 1000 ||
        right < -1000 ||
        right > 1000
    )
    {
        Drive_Stop();

        UART_Send(
            "ERR,RANGE\n");

        return;
    }


    /* 실제 모터 명령 적용 */
    Drive_Apply(
        left,
        right);


    /* watchdog timer 갱신 */
    last_drive_tick =
        HAL_GetTick();


    /* ACK 생성 */
    char reply[32];

    snprintf(
        reply,
        sizeof(reply),
        "ACK,%lu\n",
        seq);

    UART_Send(reply);
}

void HAL_UART_RxCpltCallback(
    UART_HandleTypeDef *huart)
{
    if (huart->Instance == USART2)
    {
        /*
         * Windows 계열 CRLF에 대비:
         * '\r'은 무시
         */
        if (rx_byte == '\r')
        {
            /* do nothing */
        }

        /*
         * 한 명령 완료
         */
        else if (rx_byte == '\n')
        {
            if (!line_ready)
            {
                rx_build[rx_index] =
                    '\0';

                memcpy(
                    rx_line,
                    rx_build,
                    rx_index + 1);

                line_ready = 1;
            }

            rx_index = 0;
        }

        /*
         * 일반 문자
         */
        else
        {
            if (
                rx_index
                < RX_BUF_SIZE - 1
            )
            {
                rx_build[rx_index++] =
                    (char)rx_byte;
            }
            else
            {
                /*
                 * buffer overflow이면
                 * 현재 line 폐기
                 */
                rx_index = 0;
            }
        }


        /*
         * 다음 1 byte interrupt 수신 예약.
         *
         * 이것을 빼먹으면
         * 첫 byte 이후 더 이상 수신되지 않는다.
         */
        HAL_UART_Receive_IT(
            &huart2,
            &rx_byte,
            1);
    }
}

void HAL_UART_ErrorCallback(
    UART_HandleTypeDef *huart)
{
    if (huart->Instance == USART2)
    {
        rx_index = 0;

        HAL_UART_Receive_IT(
            &huart2,
            &rx_byte,
            1);
    }
}
/* USER CODE END 4 */

/**
  * @brief  This function is executed in case of error occurrence.
  * @retval None
  */
void Error_Handler(void)
{
  /* USER CODE BEGIN Error_Handler_Debug */
  /* User can add his own implementation to report the HAL error return state */
  __disable_irq();
  while (1)
  {
  }
  /* USER CODE END Error_Handler_Debug */
}
#ifdef USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t *file, uint32_t line)
{
  /* USER CODE BEGIN 6 */
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* USER CODE END 6 */
}
#endif /* USE_FULL_ASSERT */
